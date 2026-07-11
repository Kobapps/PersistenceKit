using Microsoft.CodeAnalysis;

namespace PersistenceKit.SourceGenerator;

/// <summary>
/// Diagnostic identifiers and descriptors emitted by the persistent-state generator.
/// Identifiers are stable: PK001..PK007.
/// </summary>
internal static class Diagnostics
{
    private const string Category = "PersistenceKit";

    public static readonly DiagnosticDescriptor PK001_NotPartial = new(
        id: "PK001",
        title: "[PersistentState] class must be partial",
        messageFormat: "Class '{0}' is annotated with [PersistentState] and contains [Persist] fields, but is not declared partial. Add the 'partial' modifier so the generator can extend it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PK002_EncryptedRemote = new(
        id: "PK002",
        title: "[Encrypted] field routed to Remote",
        messageFormat: "Field '{0}' is marked [Encrypted] and routed to PersistTarget.Remote. The kit cannot inspect remote payloads, so per-field encryption usually pairs better with Json/Binary.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PK003_UnsupportedType = new(
        id: "PK003",
        title: "[Persist] field type is not supported",
        messageFormat: "Field '{0}' has type '{1}', which has no direct writer overload and falls back to WriteObject. This is fine for most user types but allocates more — consider a primitive shape if perf-sensitive.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: false);

    public static readonly DiagnosticDescriptor PK004_DuplicatePersist = new(
        id: "PK004",
        title: "Duplicate [Persist] attribute",
        messageFormat: "Field '{0}' has more than one [Persist] attribute applied.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PK005_StaticOrConst = new(
        id: "PK005",
        title: "[Persist] cannot be applied to static / const / readonly fields",
        messageFormat: "Field '{0}' is {1}; [Persist] only supports mutable instance fields.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PK006_EncryptedComplexType = new(
        id: "PK006",
        title: "[Encrypted] applied to a complex field type",
        messageFormat: "Field '{0}' has type '{1}'. Encryption applies to the leaf value only — the JSON shape will be a single string token, not an encrypted object graph. Verify this is intended.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PK007_PersistentStateNonPartial = new(
        id: "PK007",
        title: "[PersistentState] class must be partial (no [Persist] fields)",
        messageFormat: "Class '{0}' is annotated with [PersistentState] but is not declared partial. Add the 'partial' modifier.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
