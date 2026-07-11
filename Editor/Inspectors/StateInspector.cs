using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace PersistenceKit.Editor
{
    /// <summary>
    /// Reflection helpers that convert a live <see cref="IPersistentState"/> instance into
    /// an editable view model: per-field name + resolved target + encrypted flag + getter
    /// + setter. The editor window uses these views to draw the field grid.
    /// </summary>
    internal static class StateInspector
    {
        internal sealed class FieldView
        {
            public string FieldName;          // e.g. "_userId"
            public string PropertyName;       // e.g. "UserId"
            public string SerializedName;     // value used in the payload
            public Type   FieldType;
            public PersistTarget Target;
            public bool   Encrypted;
            public Func<object> Get;
            public Action<object> Set;
            public bool   ReadOnly;           // true when no public setter exists
        }

        public static List<FieldView> Inspect(IPersistentState state)
        {
            var result = new List<FieldView>();
            if (state == null) return result;
            var t = state.GetType();
            var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (var f in t.GetFields(bf))
            {
                var persist = f.GetCustomAttribute<PersistAttribute>();
                if (persist == null) continue;

                var enc = f.GetCustomAttribute<EncryptedAttribute>();
                var view = new FieldView
                {
                    FieldName      = f.Name,
                    PropertyName   = ToPascal(f.Name),
                    SerializedName = ToPascal(f.Name),
                    FieldType      = f.FieldType,
                    Encrypted      = enc != null,
                    Target         = ResolveTarget(t, f, persist),
                };

                // The generator only emits a property when the field is hidden (leading
                // underscore). When the user wrote the field publicly with a matching
                // PascalCase name (the new direct-field convention), there is no property,
                // and we write to the field then explicitly call state.MarkDirty(target).
                var prop = t.GetProperty(view.PropertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanWrite)
                {
                    var capturedState = state;
                    var capturedProp  = prop;
                    view.Get = () => capturedProp.GetValue(capturedState);
                    view.Set = v   =>
                    {
                        capturedProp.SetValue(capturedState, v);   // setter already calls MarkDirty
                        KickRuntime();
                    };
                }
                else if (prop != null)
                {
                    var capturedState = state;
                    var capturedProp  = prop;
                    view.Get = () => capturedProp.GetValue(capturedState);
                    view.Set = _  => { /* no setter */ };
                    view.ReadOnly = true;
                }
                else
                {
                    var capturedState = state;
                    var capturedField = f;
                    var capturedView  = view;
                    view.Get = () => capturedField.GetValue(capturedState);
                    view.Set = v   =>
                    {
                        capturedField.SetValue(capturedState, v);
                        // Direct-field write doesn't auto-mark — flush this target explicitly.
                        if (capturedState is IPersistentState ps) ps.MarkDirty(capturedView.Target);
                        KickRuntime();
                    };
                }

                result.Add(view);
            }
            return result;
        }

        /// <summary>
        /// Nudge Play-mode forward after an inspector-driven write so the running game
        /// reads the new value on its very next Update tick, not whenever the editor
        /// happens to render the next frame. Cheap; no-op outside Play mode.
        /// </summary>
        private static void KickRuntime()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            // Optional: nudge the editor's repaint loop too so any IMGUI containers
            // bound to the same state refresh immediately.
            SceneView.RepaintAll();
        }

        private static PersistTarget ResolveTarget(Type t, FieldInfo f, PersistAttribute persist)
        {
            if (!persist.UsesDefaultTarget) return persist.Target;

            // Generator emits a static (or const) field named __t_<original-field-name>.
            var slotField = t.GetField("__t_" + f.Name, BindingFlags.Static | BindingFlags.NonPublic);
            if (slotField == null) return PersistTarget.Json;          // hand-written fixture / unresolved
            object boxed = slotField.IsLiteral ? slotField.GetRawConstantValue() : slotField.GetValue(null);
            if (boxed is PersistTarget pt) return pt;
            return PersistTarget.Json;
        }

        private static string ToPascal(string fieldName)
        {
            var trimmed = fieldName.TrimStart('_');
            if (trimmed.Length == 0) return fieldName;
            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
        }
    }
}
