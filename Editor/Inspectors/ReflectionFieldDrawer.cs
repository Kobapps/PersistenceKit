using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PersistenceKit.Editor
{
    /// <summary>
    /// Reflection-based IMGUI drawer used for complex [Persist] field values that Unity's
    /// stock <c>[SerializeReference]</c> path can't render (notably <c>List&lt;T&gt;</c>
    /// — Unity doesn't expand SerializeReference-held generics in the inspector).
    /// </summary>
    /// <remarks>
    /// Handles primitives, enums, arrays/<see cref="IList"/>, and any class flagged with
    /// <see cref="SerializableAttribute"/> via recursive descent on its public instance
    /// fields. Foldout state is tracked per dotted path so collapsed sections survive
    /// repaints. Returns <c>true</c> from <see cref="Draw"/> when the user mutated the
    /// graph; the caller writes the mutated reference back to the underlying state.
    /// </remarks>
    internal sealed class ReflectionFieldDrawer
    {
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();

        /// <summary>Render <paramref name="value"/> typed as <paramref name="type"/>; sets <paramref name="newValue"/> and returns <c>true</c> on user edit.</summary>
        public bool Draw(string label, object value, Type type, string path, out object newValue)
        {
            newValue = value;

            // ─── Primitives ─────────────────────────────────────
            if (type == typeof(string))   return DrawText (label, (string)value, path, out newValue);
            if (type == typeof(int))      return DrawInt32(label, (int)(value ?? 0), out newValue);
            if (type == typeof(long))     return DrawInt64(label, (long)(value ?? 0L), out newValue);
            if (type == typeof(uint))     return DrawUInt32(label, (uint)(value ?? 0U), out newValue);
            if (type == typeof(ulong))    return DrawUInt64(label, (ulong)(value ?? 0UL), out newValue);
            if (type == typeof(float))    return DrawFloat(label, (float)(value ?? 0f), out newValue);
            if (type == typeof(double))   return DrawDouble(label, (double)(value ?? 0.0), out newValue);
            if (type == typeof(bool))     return DrawBool(label, (bool)(value ?? false), out newValue);
            if (type.IsEnum)              return DrawEnum(label, value as Enum ?? (Enum)Activator.CreateInstance(type), out newValue);

            // ─── Collections ────────────────────────────────────
            if (type.IsArray)
                return DrawArray(label, (Array)value, type.GetElementType(), path, out newValue);
            if (typeof(IDictionary).IsAssignableFrom(type) && type.IsGenericType)
                return DrawDictionary(label, (IDictionary)value, type,
                    type.GetGenericArguments()[0], type.GetGenericArguments()[1], path, out newValue);
            if (typeof(IList).IsAssignableFrom(type) && type.IsGenericType)
                return DrawList(label, (IList)value, type, type.GetGenericArguments()[0], path, out newValue);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
                return DrawHashSet(label, value, type, type.GetGenericArguments()[0], path, out newValue);

            // ─── Nested [Serializable] object ───────────────────
            if (type.IsClass && Attribute.IsDefined(type, typeof(SerializableAttribute)))
                return DrawObject(label, value, type, path, out newValue);

            // ─── Fallback: read-only ToString ───────────────────
            EditorGUILayout.LabelField(label, value?.ToString() ?? "<null>");
            return false;
        }

        // ─── Primitive helpers ───────────────────────────────────

        private static bool DrawText(string label, string current, string path, out object newValue)
        {
            current ??= string.Empty;
            var n = EditorGUILayout.TextField(label, current);
            newValue = n;
            return n != current;
        }
        private static bool DrawInt32(string label, int current, out object newValue)
        { var n = EditorGUILayout.IntField(label, current); newValue = n; return n != current; }
        private static bool DrawInt64(string label, long current, out object newValue)
        { var n = EditorGUILayout.LongField(label, current); newValue = n; return n != current; }
        private static bool DrawUInt32(string label, uint current, out object newValue)
        { var n = (uint)Math.Max(0, EditorGUILayout.LongField(label, current)); newValue = n; return n != current; }
        private static bool DrawUInt64(string label, ulong current, out object newValue)
        {
            var s = EditorGUILayout.TextField(label, current.ToString());
            newValue = current;
            if (ulong.TryParse(s, out var n) && n != current) { newValue = n; return true; }
            return false;
        }
        private static bool DrawFloat(string label, float current, out object newValue)
        { var n = EditorGUILayout.FloatField(label, current); newValue = n; return Math.Abs(n - current) > float.Epsilon; }
        private static bool DrawDouble(string label, double current, out object newValue)
        { var n = EditorGUILayout.DoubleField(label, current); newValue = n; return Math.Abs(n - current) > double.Epsilon; }
        private static bool DrawBool(string label, bool current, out object newValue)
        { var n = EditorGUILayout.Toggle(label, current); newValue = n; return n != current; }
        private static bool DrawEnum(string label, Enum current, out object newValue)
        { var n = EditorGUILayout.EnumPopup(label, current); newValue = n; return !Equals(n, current); }

        // ─── Collections ─────────────────────────────────────────

        private bool DrawArray(string label, Array array, Type elemType, string path, out object newValue)
        {
            newValue = array;
            if (array == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(label);
                if (GUILayout.Button($"+ new {elemType.Name}[0]", GUILayout.Width(160)))
                {
                    newValue = Array.CreateInstance(elemType, 0);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
                return false;
            }

            bool changed = false;
            bool open = GetFoldout(path, true);
            open = EditorGUILayout.Foldout(open, $"{label}    ({array.Length})", true);
            SetFoldout(path, open);
            if (!open) return false;

            EditorGUI.indentLevel++;

            int newSize = EditorGUILayout.DelayedIntField("Size", array.Length);
            if (newSize != array.Length && newSize >= 0)
            {
                var resized = Array.CreateInstance(elemType, newSize);
                Array.Copy(array, resized, Math.Min(array.Length, newSize));
                array = resized;
                newValue = array;
                changed = true;
            }

            for (int i = 0; i < array.Length; i++)
            {
                if (Draw($"  [{i}]", array.GetValue(i), elemType, $"{path}[{i}]", out var nv))
                {
                    array.SetValue(nv, i);
                    changed = true;
                }
            }

            EditorGUI.indentLevel--;
            return changed;
        }

        private bool DrawList(string label, IList list, Type listType, Type elemType, string path, out object newValue)
        {
            newValue = list;
            if (list == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(label);
                if (GUILayout.Button($"+ new List<{elemType.Name}>", GUILayout.Width(160)))
                {
                    newValue = Activator.CreateInstance(listType);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
                return false;
            }

            bool changed = false;
            bool open = GetFoldout(path, true);
            open = EditorGUILayout.Foldout(open, $"{label}    ({list.Count})", true);
            SetFoldout(path, open);
            if (!open) return false;

            EditorGUI.indentLevel++;

            int removeIdx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.BeginVertical();
                if (Draw($"  [{i}]", list[i], elemType, $"{path}[{i}]", out var nv))
                {
                    list[i] = nv;
                    changed = true;
                }
                EditorGUILayout.EndVertical();
                if (GUILayout.Button("×", GUILayout.Width(22), GUILayout.Height(18)))
                    removeIdx = i;
                EditorGUILayout.EndHorizontal();
            }
            if (removeIdx >= 0) { list.RemoveAt(removeIdx); changed = true; }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ Add", GUILayout.Width(80)))
            {
                list.Add(CreateInstance(elemType));
                changed = true;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
            return changed;
        }

        // ─── Dictionaries ────────────────────────────────────────

        private bool DrawDictionary(string label, IDictionary dict, Type dictType, Type keyType, Type valueType,
                                    string path, out object newValue)
        {
            newValue = dict;
            if (dict == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(label);
                if (GUILayout.Button($"+ new Dictionary<{keyType.Name}, {valueType.Name}>", GUILayout.Width(220)))
                {
                    newValue = Activator.CreateInstance(dictType);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
                return false;
            }

            bool changed = false;
            bool open = GetFoldout(path, true);
            open = EditorGUILayout.Foldout(open, $"{label}    ({dict.Count})", true);
            SetFoldout(path, open);
            if (!open) return false;

            EditorGUI.indentLevel++;

            // Snapshot keys — modifying during iteration throws.
            var keys = new object[dict.Count];
            int ki = 0;
            foreach (var k in dict.Keys) keys[ki++] = k;

            object removeKey = null;
            foreach (var k in keys)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"  {k}", GUILayout.Width(140));   // keys read-only
                EditorGUILayout.BeginVertical();
                if (Draw("=", dict[k], valueType, $"{path}[{k}]", out var nv))
                {
                    dict[k] = nv;
                    changed = true;
                }
                EditorGUILayout.EndVertical();
                if (GUILayout.Button("×", GUILayout.Width(22), GUILayout.Height(18)))
                    removeKey = k;
                EditorGUILayout.EndHorizontal();
            }
            if (removeKey != null) { dict.Remove(removeKey); changed = true; }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ Add Default", GUILayout.Width(120)))
            {
                var k = GenerateUniqueKey(keyType, dict);
                if (k != null && !dict.Contains(k))
                {
                    dict.Add(k, CreateInstance(valueType));
                    changed = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
            return changed;
        }

        private static object GenerateUniqueKey(Type keyType, IDictionary dict)
        {
            // Cheap defaults — string gets "key_N" until unique; int picks max+1; otherwise default(T).
            if (keyType == typeof(string))
            {
                int n = dict.Count + 1;
                while (true)
                {
                    var candidate = "key_" + n;
                    if (!dict.Contains(candidate)) return candidate;
                    n++;
                }
            }
            if (keyType == typeof(int))
            {
                int max = -1;
                foreach (var k in dict.Keys) if (k is int i && i > max) max = i;
                return max + 1;
            }
            try { return Activator.CreateInstance(keyType); }
            catch { return null; }
        }

        // ─── HashSet<T> ──────────────────────────────────────────

        private bool DrawHashSet(string label, object setObj, Type setType, Type elemType, string path, out object newValue)
        {
            newValue = setObj;
            if (setObj == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(label);
                if (GUILayout.Button($"+ new HashSet<{elemType.Name}>", GUILayout.Width(220)))
                {
                    newValue = Activator.CreateInstance(setType);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
                return false;
            }

            // Snapshot items so the GUI can issue Remove without invalidating the iterator.
            var items = new List<object>();
            foreach (var x in (IEnumerable)setObj) items.Add(x);

            bool changed = false;
            bool open = GetFoldout(path, true);
            open = EditorGUILayout.Foldout(open, $"{label}    ({items.Count})", true);
            SetFoldout(path, open);
            if (!open) return false;

            EditorGUI.indentLevel++;

            var removeMethod = setType.GetMethod("Remove", new[] { elemType });
            var addMethod    = setType.GetMethod("Add",    new[] { elemType });

            object removeItem = null;
            for (int i = 0; i < items.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"  - {items[i] ?? "<null>"}");
                if (GUILayout.Button("×", GUILayout.Width(22), GUILayout.Height(18)))
                    removeItem = items[i];
                EditorGUILayout.EndHorizontal();
            }
            if (removeItem != null && removeMethod != null)
            {
                removeMethod.Invoke(setObj, new[] { removeItem });
                changed = true;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ Add Default", GUILayout.Width(120)) && addMethod != null)
            {
                addMethod.Invoke(setObj, new[] { CreateInstance(elemType) });
                changed = true;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
            return changed;
        }

        // ─── Nested objects ──────────────────────────────────────

        private bool DrawObject(string label, object value, Type type, string path, out object newValue)
        {
            newValue = value;
            if (value == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(label);
                if (GUILayout.Button($"+ new {type.Name}", GUILayout.Width(160)))
                {
                    newValue = CreateInstance(type);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
                return false;
            }

            bool changed = false;
            bool open = GetFoldout(path, true);
            open = EditorGUILayout.Foldout(open, $"{label}    {type.Name}", true);
            SetFoldout(path, open);
            if (!open) return false;

            EditorGUI.indentLevel++;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsNotSerialized) continue;
                var fv = f.GetValue(value);
                if (Draw(f.Name, fv, f.FieldType, $"{path}.{f.Name}", out var nv))
                {
                    f.SetValue(value, nv);
                    changed = true;
                }
            }
            EditorGUI.indentLevel--;
            return changed;
        }

        private static object CreateInstance(Type type)
        {
            if (type == typeof(string)) return string.Empty;
            try { return Activator.CreateInstance(type); }
            catch { return type.IsValueType ? Activator.CreateInstance(type) : null; }
        }

        private bool GetFoldout(string path, bool defaultOpen)
            => _foldouts.TryGetValue(path, out var v) ? v : defaultOpen;

        private void SetFoldout(string path, bool value) => _foldouts[path] = value;
    }
}
