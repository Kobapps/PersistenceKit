using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PersistenceKit.SourceGenerator;

/// <summary>
/// Source generator for <c>[PersistentState]</c>-marked classes. For each such class it
/// emits a partial implementing <c>IPersistentState</c>, plus per-field properties, the
/// per-target dispatch, and a <c>RuntimeInitializeOnLoadMethod</c>-driven registration hook.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PersistentStateGenerator : IIncrementalGenerator
{
    private const string PersistentStateAttr = "PersistenceKit.PersistentStateAttribute";
    private const string PersistAttr         = "PersistenceKit.PersistAttribute";
    private const string EncryptedAttr       = "PersistenceKit.EncryptedAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var states = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PersistentStateAttr,
                predicate: static (n, _) => n is ClassDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static m => m is not null)!;

        context.RegisterSourceOutput(states, static (spc, model) => Emit(spc, model!));
    }

    private static StateModel? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var classDecl = (ClassDeclarationSyntax)ctx.TargetNode;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        var isPartial = classDecl.Modifiers.Any(m => m.Text == "partial");
        var fields = new List<FieldModel>();

        foreach (var member in symbol.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsImplicitlyDeclared) continue;

            var attrs = field.GetAttributes();
            var persistAttrs = attrs.Where(a => a.AttributeClass?.ToDisplayString() == PersistAttr).ToList();
            if (persistAttrs.Count == 0) continue;

            // PK004: duplicate
            if (persistAttrs.Count > 1)
                diagnostics.Add(new DiagnosticInfo(Diagnostics.PK004_DuplicatePersist, field.Locations.FirstOrDefault(), field.Name));

            // PK005: static/const/readonly
            if (field.IsStatic || field.IsConst || field.IsReadOnly)
            {
                var what = field.IsConst ? "const" : field.IsStatic ? "static" : "readonly";
                diagnostics.Add(new DiagnosticInfo(Diagnostics.PK005_StaticOrConst, field.Locations.FirstOrDefault(), field.Name, what));
                continue;
            }

            var persist = persistAttrs[0];
            bool usesDefault = true;
            string explicitTarget = "Json";
            // ctor(PersistTarget target) → ctor args length 1 → not default
            if (persist.ConstructorArguments.Length == 1)
            {
                usesDefault = false;
                // PersistTarget has byte underlying type, so TypedConstant.Value is a boxed
                // byte — switching against int literals never matches. Convert to int first.
                int v = Convert.ToInt32(persist.ConstructorArguments[0].Value ?? 0);
                explicitTarget = v switch
                {
                    0 => "Json",
                    1 => "Binary",
                    2 => "PlayerPrefs",
                    3 => "Remote",
                    _ => "Json",
                };
            }
            // Optional Name property
            string serializedName = field.Name.TrimStart('_');
            serializedName = char.ToUpperInvariant(serializedName[0]) + serializedName.Substring(1);
            foreach (var na in persist.NamedArguments)
            {
                if (na.Key == "Name" && na.Value.Value is string s && !string.IsNullOrEmpty(s))
                    serializedName = s;
            }

            bool encrypted = false;
            string keyPurpose = "default";
            var encAttr = attrs.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == EncryptedAttr);
            if (encAttr is not null)
            {
                encrypted = true;
                foreach (var na in encAttr.NamedArguments)
                    if (na.Key == "KeyPurpose" && na.Value.Value is string s && !string.IsNullOrEmpty(s))
                        keyPurpose = s;
            }

            var typeInfo = ResolveType(field.Type);
            if (encrypted && !typeInfo.IsLeafFriendlyForEncryption)
                diagnostics.Add(new DiagnosticInfo(Diagnostics.PK006_EncryptedComplexType, field.Locations.FirstOrDefault(), field.Name, field.Type.ToDisplayString()));

            // PK002: encrypted on remote-only field
            if (encrypted && !usesDefault && explicitTarget == "Remote")
                diagnostics.Add(new DiagnosticInfo(Diagnostics.PK002_EncryptedRemote, field.Locations.FirstOrDefault(), field.Name));

            fields.Add(new FieldModel(
                FieldName:      field.Name,
                PropertyName:   ToPascal(field.Name),
                SerializedName: serializedName,
                TypeFqn:        field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeKind:       typeInfo.Kind,
                EnumUnderlying: typeInfo.EnumUnderlyingFqn,
                UsesDefault:    usesDefault,
                ExplicitTarget: explicitTarget,
                Encrypted:      encrypted,
                KeyPurpose:     keyPurpose));
        }

        if (fields.Count > 0 && !isPartial)
            diagnostics.Add(new DiagnosticInfo(Diagnostics.PK001_NotPartial, classDecl.Identifier.GetLocation(), symbol.Name));
        else if (fields.Count == 0 && !isPartial)
            diagnostics.Add(new DiagnosticInfo(Diagnostics.PK007_PersistentStateNonPartial, classDecl.Identifier.GetLocation(), symbol.Name));

        // Read TypeId override from the class attribute, if any.
        string typeId = symbol.Name;
        var classAttr = symbol.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == PersistentStateAttr);
        foreach (var na in classAttr.NamedArguments)
            if (na.Key == "TypeId" && na.Value.Value is string s && !string.IsNullOrEmpty(s))
                typeId = s;

        return new StateModel(
            Namespace:    symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
            ClassName:    symbol.Name,
            FullName:     symbol.ToDisplayString(),
            TypeId:       typeId,
            IsPartial:    isPartial,
            Fields:       fields.ToImmutableArray(),
            Diagnostics:  diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext spc, StateModel model)
    {
        foreach (var d in model.Diagnostics)
            spc.ReportDiagnostic(Diagnostic.Create(d.Descriptor, d.Location, d.Args));

        if (!model.IsPartial) return;       // can't extend a non-partial — already reported.
        if (model.Fields.Length == 0)
        {
            // Still emit a degenerate partial so [PersistentState] without any [Persist] fields
            // still implements IPersistentState (target mask = None — never serialized).
        }

        var hint = model.FullName.Replace('<', '_').Replace('>', '_').Replace('.', '_') + ".g.cs";
        var sb = new StringBuilder(4096);

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("using System;");
        sb.AppendLine("using PersistenceKit;");
        sb.AppendLine();

        if (model.Namespace != null)
        {
            sb.Append("namespace ").Append(model.Namespace).AppendLine();
            sb.AppendLine("{");
        }

        sb.Append("    partial class ").Append(model.ClassName).AppendLine(" : global::PersistenceKit.IPersistentState");
        sb.AppendLine("    {");
        EmitBindAndKey(sb, model);
        EmitTargetSlots(sb, model);
        EmitProperties(sb, model);
        EmitWritePayload(sb, model);
        EmitReadPayload(sb, model);
        EmitMaskAndResolve(sb, model);
        EmitMarkDirty(sb, model);
        EmitRegistration(sb, model);
        sb.AppendLine("    }");

        if (model.Namespace != null) sb.AppendLine("}");

        spc.AddSource(hint, sb.ToString());
    }

    private static void EmitBindAndKey(StringBuilder sb, StateModel m)
    {
        sb.Append("        public const string __TypeId = \"").Append(m.TypeId).AppendLine("\";");
        sb.AppendLine("        private string __slot = string.Empty;");
        sb.AppendLine("        private string __cachedKey = __TypeId;   // computed once at Bind() to avoid per-mutation allocation.");
        sb.AppendLine("        private System.Action<global::PersistenceKit.PersistTarget> __markDirty;");
        sb.AppendLine();
        sb.AppendLine("        string global::PersistenceKit.IPersistentState.Key => __cachedKey;");
        sb.AppendLine();
        sb.AppendLine("        void global::PersistenceKit.IPersistentState.Bind(string slot, System.Action<global::PersistenceKit.PersistTarget> markDirty)");
        sb.AppendLine("        {");
        sb.AppendLine("            __slot       = slot ?? string.Empty;");
        sb.AppendLine("            __cachedKey  = __slot.Length == 0 ? __TypeId : (__TypeId + \":\" + __slot);");
        sb.AppendLine("            __markDirty  = markDirty;");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitTargetSlots(StringBuilder sb, StateModel m)
    {
        foreach (var f in m.Fields)
        {
            if (f.UsesDefault)
                sb.Append("        private static global::PersistenceKit.PersistTarget __t_").Append(f.FieldName).AppendLine(";");
            else
                sb.Append("        private const  global::PersistenceKit.PersistTarget __t_").Append(f.FieldName)
                  .Append(" = global::PersistenceKit.PersistTarget.").Append(f.ExplicitTarget).AppendLine(";");
        }
        sb.AppendLine();
    }

    private static void EmitProperties(StringBuilder sb, StateModel m)
    {
        foreach (var f in m.Fields)
        {
            // Only emit a public property when the field is "hidden" (leading underscore
            // and/or different casing). When the user already named the field publicly
            // (e.g. `public string DisplayName;`), the field IS the access path — emitting
            // a property would clash. In that case the user is expected to call MarkDirty()
            // after batching mutations.
            if (f.FieldName == f.PropertyName) continue;

            sb.Append("        public ").Append(f.TypeFqn).Append(' ').Append(f.PropertyName).AppendLine();
            sb.AppendLine("        {");
            sb.Append("            get => ").Append(f.FieldName).AppendLine(";");
            sb.AppendLine("            set");
            sb.AppendLine("            {");
            sb.Append("                ").Append(f.FieldName).AppendLine(" = value;");
            sb.Append("                __markDirty?.Invoke(__t_").Append(f.FieldName).AppendLine(");");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }
    }

    private static void EmitWritePayload(StringBuilder sb, StateModel m)
    {
        sb.AppendLine("        void global::PersistenceKit.IPersistentState.WritePayload(global::PersistenceKit.PersistTarget target, global::PersistenceKit.IPayloadWriter writer)");
        sb.AppendLine("        {");
        foreach (var f in m.Fields)
        {
            sb.Append("            if (target == __t_").Append(f.FieldName).Append(") ");
            EmitWriterCall(sb, f);
            sb.AppendLine();
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitWriterCall(StringBuilder sb, FieldModel f)
    {
        var enc = f.Encrypted ? "true" : "false";
        switch (f.TypeKind)
        {
            case TypeKindKind.String:  sb.Append("writer.WriteString(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(" ?? string.Empty, ").Append(enc).Append(");"); break;
            case TypeKindKind.Bool:    sb.Append("writer.WriteBool(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.Int32:   sb.Append("writer.WriteInt32(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.Int64:   sb.Append("writer.WriteInt64(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.UInt32:  sb.Append("writer.WriteUInt32(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.UInt64:  sb.Append("writer.WriteUInt64(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.Single:  sb.Append("writer.WriteSingle(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.Double:  sb.Append("writer.WriteDouble(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.Bytes:   sb.Append("writer.WriteBytes(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            case TypeKindKind.Enum:    sb.Append("writer.WriteEnum<").Append(f.TypeFqn).Append(">(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", ").Append(enc).Append(");"); break;
            default:                   sb.Append("writer.WriteObject(\"").Append(f.SerializedName).Append("\", ").Append(f.FieldName).Append(", typeof(").Append(f.TypeFqn).Append("), ").Append(enc).Append(");"); break;
        }
    }

    private static void EmitReadPayload(StringBuilder sb, StateModel m)
    {
        sb.AppendLine("        void global::PersistenceKit.IPersistentState.ReadPayload(global::PersistenceKit.PersistTarget target, global::PersistenceKit.IPayloadReader reader)");
        sb.AppendLine("        {");
        foreach (var f in m.Fields)
        {
            var enc = f.Encrypted ? "true" : "false";
            sb.Append("            if (target == __t_").Append(f.FieldName).Append(" && ");
            switch (f.TypeKind)
            {
                case TypeKindKind.String:
                    sb.Append("reader.ReadString(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Bool:
                    sb.Append("reader.ReadBool(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Int32:
                    sb.Append("reader.ReadInt32(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Int64:
                    sb.Append("reader.ReadInt64(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.UInt32:
                    sb.Append("reader.ReadUInt32(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.UInt64:
                    sb.Append("reader.ReadUInt64(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Single:
                    sb.Append("reader.ReadSingle(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Double:
                    sb.Append("reader.ReadDouble(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Bytes:
                    sb.Append("reader.ReadBytes(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                case TypeKindKind.Enum:
                    sb.Append("reader.ReadEnum<").Append(f.TypeFqn).Append(">(\"").Append(f.SerializedName).Append("\", ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = __v_").Append(f.FieldName).Append(";"); break;
                default:
                    sb.Append("reader.ReadObject(\"").Append(f.SerializedName).Append("\", typeof(").Append(f.TypeFqn).Append("), ").Append(enc).Append(", out var __v_").Append(f.FieldName).Append(")) ").Append(f.FieldName).Append(" = (").Append(f.TypeFqn).Append(")__v_").Append(f.FieldName).Append(";"); break;
            }
            sb.AppendLine();
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitMaskAndResolve(StringBuilder sb, StateModel m)
    {
        // Seed mask from explicit-target fields. Default-target fields OR'd in by ResolveDefaults.
        byte seed = 0;
        foreach (var f in m.Fields)
            if (!f.UsesDefault) seed |= (byte)(1 << TargetIndex(f.ExplicitTarget));
        sb.Append("        private static global::PersistenceKit.PersistTargetMask __mask = (global::PersistenceKit.PersistTargetMask)").Append(seed).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("        global::PersistenceKit.PersistTargetMask global::PersistenceKit.IPersistentState.TargetMask => __mask;");
        sb.AppendLine();
        sb.AppendLine("        public static void __ResolveDefaults(global::PersistenceKit.PersistTarget defaultTarget)");
        sb.AppendLine("        {");
        foreach (var f in m.Fields)
            if (f.UsesDefault) sb.Append("            __t_").Append(f.FieldName).AppendLine(" = defaultTarget;");
        sb.AppendLine("            __mask = (global::PersistenceKit.PersistTargetMask)((byte)__mask | (byte)(1 << (int)defaultTarget));");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static int TargetIndex(string target) => target switch
    {
        "Json" => 0, "Binary" => 1, "PlayerPrefs" => 2, "Remote" => 3, _ => 0,
    };

    private static void EmitMarkDirty(StringBuilder sb, StateModel m)
    {
        // Public methods so user code can call state.MarkDirty() directly without an
        // explicit cast — they implicitly satisfy IPersistentState.MarkDirty.
        sb.AppendLine("        /// <summary>Mark every target in this state's mask dirty.</summary>");
        sb.AppendLine("        public void MarkDirty()");
        sb.AppendLine("        {");
        sb.AppendLine("            var mask = (byte)__mask;");
        sb.AppendLine("            if ((mask & 1) != 0) __markDirty?.Invoke(global::PersistenceKit.PersistTarget.Json);");
        sb.AppendLine("            if ((mask & 2) != 0) __markDirty?.Invoke(global::PersistenceKit.PersistTarget.Binary);");
        sb.AppendLine("            if ((mask & 4) != 0) __markDirty?.Invoke(global::PersistenceKit.PersistTarget.PlayerPrefs);");
        sb.AppendLine("            if ((mask & 8) != 0) __markDirty?.Invoke(global::PersistenceKit.PersistTarget.Remote);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>Mark a single target dirty.</summary>");
        sb.AppendLine("        public void MarkDirty(global::PersistenceKit.PersistTarget target) => __markDirty?.Invoke(target);");
        sb.AppendLine();
    }

    private static void EmitRegistration(StringBuilder sb, StateModel m)
    {
        sb.AppendLine("#if UNITY_EDITOR");
        sb.AppendLine("        [UnityEditor.InitializeOnLoadMethod]");
        sb.AppendLine("#endif");
        sb.AppendLine("        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]");
        sb.AppendLine("        internal static void __Register()");
        sb.AppendLine("        {");
        sb.Append("            global::PersistenceKit.PersistentStateRegistry.Register<").Append(m.ClassName).Append(">(static () => new ").Append(m.ClassName).AppendLine("(), __ResolveDefaults, __TypeId);");
        sb.AppendLine("        }");
    }

    private static string ToPascal(string fieldName)
    {
        var trimmed = fieldName.TrimStart('_');
        if (trimmed.Length == 0) return fieldName;
        return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }

    private static (TypeKindKind Kind, string? EnumUnderlyingFqn, bool IsLeafFriendlyForEncryption) ResolveType(ITypeSymbol t)
    {
        if (t.TypeKind == TypeKind.Enum)
            return (TypeKindKind.Enum, ((INamedTypeSymbol)t).EnumUnderlyingType?.ToDisplayString(), true);

        return t.SpecialType switch
        {
            SpecialType.System_String  => (TypeKindKind.String, null, true),
            SpecialType.System_Boolean => (TypeKindKind.Bool,   null, true),
            SpecialType.System_Int32   => (TypeKindKind.Int32,  null, true),
            SpecialType.System_Int64   => (TypeKindKind.Int64,  null, true),
            SpecialType.System_UInt32  => (TypeKindKind.UInt32, null, true),
            SpecialType.System_UInt64  => (TypeKindKind.UInt64, null, true),
            SpecialType.System_Single  => (TypeKindKind.Single, null, true),
            SpecialType.System_Double  => (TypeKindKind.Double, null, true),
            _ when IsByteArray(t)      => (TypeKindKind.Bytes,  null, true),
            _                          => (TypeKindKind.Object, null, false),
        };
    }

    private static bool IsByteArray(ITypeSymbol t)
        => t is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte;

    private enum TypeKindKind { String, Bool, Int32, Int64, UInt32, UInt64, Single, Double, Bytes, Enum, Object }

    private sealed record FieldModel(
        string FieldName,
        string PropertyName,
        string SerializedName,
        string TypeFqn,
        TypeKindKind TypeKind,
        string? EnumUnderlying,
        bool UsesDefault,
        string ExplicitTarget,
        bool Encrypted,
        string KeyPurpose);

    private sealed record StateModel(
        string? Namespace,
        string ClassName,
        string FullName,
        string TypeId,
        bool IsPartial,
        ImmutableArray<FieldModel> Fields,
        ImmutableArray<DiagnosticInfo> Diagnostics);

    private readonly record struct DiagnosticInfo(DiagnosticDescriptor Descriptor, Location? Location, params object[] Args);
}
