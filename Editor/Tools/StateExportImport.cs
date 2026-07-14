using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PersistenceKit.Editor.Tools
{
    /// <summary>
    /// Exports / imports every loaded state to a single JSON file. The shape is a flat
    /// dictionary keyed by the state's storage key — values are the field name → value
    /// pairs. Encrypted fields are exported as plaintext so the file is portable and
    /// diffable; on import the kit re-applies encryption automatically because its
    /// serializer reads each field's <see cref="EncryptedAttribute"/> at save time.
    /// </summary>
    /// <remarks>
    /// Wire format:
    /// <code>
    /// {
    ///   "PlayerProfile:slot1": {
    ///     "DisplayName": "kobi",
    ///     "Level": 5,
    ///     "AuthToken": "letmein",     // PLAINTEXT in the export
    ///     "Stats": { "kills": 12 },
    ///     "Achievements": ["first_blood"]
    ///   },
    ///   "InventoryState": {
    ///     "Coins": 100,
    ///     "Items": [ ... ]
    ///   }
    /// }
    /// </code>
    /// No metadata (versioning, type ids, target/encryption flags) is written — that
    /// information lives in the state class definitions and is recovered at import time
    /// from the live <c>StateInspector.Inspect</c> walk.
    /// </remarks>
    internal static class StateExportImport
    {
        // ─── Export ───────────────────────────────────────────────

        public static int ExportAll(string filePath)
        {
            var doc = new JObject();

            foreach (var m in PersistenceManager.ActiveManagers)
            foreach (var s in m.SnapshotCache())
                doc[s.Key] = BuildFieldsObject(s);

            File.WriteAllText(filePath, doc.ToString(Formatting.Indented));
            return doc.Count;
        }

        private static JObject BuildFieldsObject(IPersistentState state)
        {
            var fields = StateInspector.Inspect(state);
            var obj    = new JObject();

            foreach (var fv in fields)
            {
                object value = null;
                try { value = fv.Get(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PersistenceKit] Export: read failed for {state.GetType().Name}.{fv.PropertyName}: {ex.Message}");
                }
                // [Encrypted] fields export their in-memory plaintext — encryption only
                // happens when the kit serializes back to a target.
                obj[fv.SerializedName] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            return obj;
        }

        // ─── Import ───────────────────────────────────────────────

        public static async Task<(int imported, int skipped)> ImportAllAsync(string filePath)
        {
            var json = File.ReadAllText(filePath);
            JObject doc;
            try { doc = JObject.Parse(json); }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceKit] Import failed: invalid JSON — {ex.Message}");
                return (0, 0);
            }

            int imported = 0, skipped = 0;

            foreach (var prop in doc.Properties())
            {
                var key = prop.Name;
                if (!(prop.Value is JObject fieldsObj))
                {
                    Debug.LogWarning($"[PersistenceKit] Import: '{key}' is not an object — skipped.");
                    skipped++;
                    continue;
                }

                var (manager, target) = FindStateByKey(key);
                if (target == null)
                {
                    Debug.LogWarning($"[PersistenceKit] Import: state '{key}' is not currently loaded — skipped. " +
                                     "Load it once via your runtime code (LoadOrCreateAsync) and try again.");
                    skipped++;
                    continue;
                }

                var fields = StateInspector.Inspect(target);
                foreach (var fv in fields)
                {
                    if (!fieldsObj.TryGetValue(fv.SerializedName, out var token)) continue;
                    try
                    {
                        if (token == null || token.Type == JTokenType.Null)
                        {
                            if (!fv.FieldType.IsValueType) fv.Set(null);
                            // null assignment to a value type would throw — leave it alone.
                        }
                        else
                        {
                            var value = token.ToObject(fv.FieldType);
                            fv.Set(value);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[PersistenceKit] Import: failed to restore {target.GetType().Name}.{fv.PropertyName}: {ex.Message}");
                    }
                }

                // Mark every wired target dirty as a belt-and-suspenders against fields whose
                // imported JSON happened to equal the current value (no setter dirty fires).
                // Strictly the wired ones: IPersistentState.MarkDirty() sets the state's whole
                // mask, and SaveAsync throws on a target with no store behind it — which would
                // lose the import for the targets that are wired.
                PersistenceKitWindow.MarkWiredTargetsDirty(manager, target);
                await manager.SaveAsync(target);
                imported++;
            }

            return (imported, skipped);
        }

        // ─── Helpers ──────────────────────────────────────────────

        private static (PersistenceManager manager, IPersistentState state) FindStateByKey(string key)
        {
            foreach (var m in PersistenceManager.ActiveManagers)
                foreach (var s in m.SnapshotCache())
                    if (s.Key == key) return (m, s);
            return (null, null);
        }
    }
}
