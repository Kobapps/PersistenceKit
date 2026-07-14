using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PersistenceKit.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace PersistenceKit.Editor.Snapshots
{
    /// <summary>
    /// Whole-world state snapshots — one JSON file per snapshot, written under the folder
    /// configured at <c>Edit → Project Settings → PersistenceKit → Snapshots</c>. Default
    /// folder is <c>&lt;ProjectRoot&gt;/PersistenceKitSnapshots/</c>; lives outside
    /// <c>Assets/</c> (no Unity import overhead) and outside <c>Library/</c> (survives cache
    /// wipes between test runs).
    /// </summary>
    /// <remarks>
    /// File shape (one snapshot per file):
    /// <code>
    /// {
    ///   "Id": "guid",
    ///   "Label": "Pre-encryption test",
    ///   "CapturedAt": "2026-05-11T12:00:00Z",
    ///   "States": {
    ///     "PlayerProfile:slot1": { "DisplayName": "kobi", ... },
    ///     "InventoryState":      { "Coins": 100, ... }
    ///   }
    /// }
    /// </code>
    /// File names use <c>{sanitised-label}_{yyyyMMdd-HHmmss}_{id8}.json</c> so the folder
    /// is browseable / diffable without opening each file.
    /// </remarks>
    internal static class SnapshotStore
    {
        public sealed class Snapshot
        {
            public string  Id;
            public string  Label;
            public string  CapturedAt;
            public JObject States;       // key → field dict (export wire format)

            [JsonIgnore] public string FilePath;
            [JsonIgnore] public int    StateCount => States?.Count ?? 0;
        }

        private static List<Snapshot> _cache;
        private static string         _cachedFolderPath;

        /// <summary>Fired after a snapshot is captured. Arg is the new snapshot.</summary>
        public static event Action<Snapshot> OnCaptured;

        /// <summary>Fired before a restore begins. Subscribers can suppress per-state save logging.</summary>
        public static event Action<Snapshot> OnRestoring;

        /// <summary>Fired after a restore completes. Args: (snapshot, restored count, skipped count).</summary>
        public static event Action<Snapshot, int, int> OnRestored;

        // ─── Folder resolution ────────────────────────────────────

        /// <summary>Absolute path to the snapshot folder, derived from Project Settings.</summary>
        public static string FolderPath
        {
            get
            {
                var raw = PersistenceKitSettings.Instance.SnapshotsFolder;
                if (string.IsNullOrWhiteSpace(raw)) raw = "PersistenceKitSnapshots";
                if (Path.IsPathRooted(raw)) return Path.GetFullPath(raw);
                // Relative — resolve against the project root (one level up from Assets/).
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.GetFullPath(Path.Combine(projectRoot, raw));
            }
        }

        // ─── Public API ───────────────────────────────────────────

        public static List<Snapshot> All()
        {
            LoadIfNeeded();
            return new List<Snapshot>(_cache);
        }

        /// <summary>Force a re-scan of the folder on the next call.</summary>
        public static void InvalidateCache()
        {
            _cache = null;
            _cachedFolderPath = null;
        }

        /// <summary>Capture every loaded state across every active manager into one snapshot.</summary>
        public static Snapshot CaptureWorld(string label)
        {
            LoadIfNeeded();

            var states = new JObject();
            int captured = 0;
            foreach (var m in PersistenceManager.ActiveManagers)
            {
                foreach (var s in m.SnapshotCache())
                {
                    states[s.Key] = BuildFieldsObject(s);
                    captured++;
                }
            }
            if (captured == 0)
                Debug.LogWarning("[PersistenceKit] Snapshot capture: no loaded states — capturing empty snapshot anyway.");

            var snap = new Snapshot
            {
                Id         = Guid.NewGuid().ToString("N"),
                Label      = string.IsNullOrWhiteSpace(label) ? "untitled" : label.Trim(),
                CapturedAt = DateTime.UtcNow.ToString("o"),
                States     = states,
            };

            PersistOne(snap);
            _cache.Add(snap);
            try { OnCaptured?.Invoke(snap); }
            catch (Exception ex) { Debug.LogException(ex); }
            return snap;
        }

        /// <summary>
        /// Restore every state from <paramref name="snapshotId"/> back into the matching live
        /// instance. States that aren't currently loaded are skipped with a warning.
        /// </summary>
        public static async Task<(int restored, int skipped)> RestoreAsync(string snapshotId)
        {
            LoadIfNeeded();
            var snap = _cache.FirstOrDefault(s => s.Id == snapshotId);
            if (snap == null) return (0, 0);

            try { OnRestoring?.Invoke(snap); }
            catch (Exception ex) { Debug.LogException(ex); }

            int restored = 0, skipped = 0;
            if (snap.States != null)
            {
                foreach (var prop in snap.States.Properties())
                {
                    var key = prop.Name;
                    if (!(prop.Value is JObject fieldsObj))
                    {
                        skipped++;
                        continue;
                    }

                    var (manager, target) = FindStateByKey(key);
                    if (target == null)
                    {
                        Debug.LogWarning($"[PersistenceKit] Snapshot restore: state '{key}' is not currently loaded — skipped.");
                        skipped++;
                        continue;
                    }

                    ApplyFields(target, fieldsObj);
                    // Only targets with a store behind them — MarkDirty() would set the state's
                    // whole mask, and SaveAsync throws on an unwired target, which would sink
                    // the restore for the wired ones too.
                    PersistenceKitWindow.MarkWiredTargetsDirty(manager, target);
                    await manager.SaveAsync(target);
                    restored++;
                }
            }
            try { OnRestored?.Invoke(snap, restored, skipped); }
            catch (Exception ex) { Debug.LogException(ex); }
            return (restored, skipped);
        }

        public static bool Delete(string snapshotId)
        {
            LoadIfNeeded();
            var snap = _cache.FirstOrDefault(s => s.Id == snapshotId);
            if (snap == null) return false;
            try
            {
                if (!string.IsNullOrEmpty(snap.FilePath) && File.Exists(snap.FilePath))
                    File.Delete(snap.FilePath);
            }
            catch (Exception ex) { Debug.LogWarning($"[PersistenceKit] Snapshot delete: {ex.Message}"); }
            _cache.Remove(snap);
            return true;
        }

        public static void RevealFolderInFinder()
        {
            var dir = FolderPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        public static void RevealSnapshotInFinder(string snapshotId)
        {
            LoadIfNeeded();
            var snap = _cache.FirstOrDefault(s => s.Id == snapshotId);
            if (snap == null) return;
            if (!string.IsNullOrEmpty(snap.FilePath) && File.Exists(snap.FilePath))
                EditorUtility.RevealInFinder(snap.FilePath);
            else
                RevealFolderInFinder();
        }

        // ─── Field plumbing (mirrors the Export wire format) ──────

        private static JObject BuildFieldsObject(IPersistentState state)
        {
            var fields = StateInspector.Inspect(state);
            var obj = new JObject();
            foreach (var fv in fields)
            {
                object value = null;
                try { value = fv.Get(); }
                catch (Exception ex) { Debug.LogWarning($"[PersistenceKit] Snapshot read failed on {state.GetType().Name}.{fv.PropertyName}: {ex.Message}"); }
                obj[fv.SerializedName] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            return obj;
        }

        private static void ApplyFields(IPersistentState target, JObject fieldsObj)
        {
            var fields = StateInspector.Inspect(target);
            foreach (var fv in fields)
            {
                if (!fieldsObj.TryGetValue(fv.SerializedName, out var token)) continue;
                try
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        if (!fv.FieldType.IsValueType) fv.Set(null);
                    }
                    else
                    {
                        fv.Set(token.ToObject(fv.FieldType));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PersistenceKit] Snapshot apply: {target.GetType().Name}.{fv.PropertyName} failed — {ex.Message}");
                }
            }
        }

        private static (PersistenceManager manager, IPersistentState state) FindStateByKey(string key)
        {
            foreach (var m in PersistenceManager.ActiveManagers)
                foreach (var s in m.SnapshotCache())
                    if (s.Key == key) return (m, s);
            return (null, null);
        }

        // ─── Disk I/O ─────────────────────────────────────────────

        private static void LoadIfNeeded()
        {
            var current = FolderPath;
            if (_cache != null && _cachedFolderPath == current) return;
            _cachedFolderPath = current;
            _cache = new List<Snapshot>();

            try
            {
                if (!Directory.Exists(current)) return;
                foreach (var file in Directory.EnumerateFiles(current, "*.json"))
                {
                    try
                    {
                        var text = File.ReadAllText(file);
                        var s = JsonConvert.DeserializeObject<Snapshot>(text);
                        if (s != null && !string.IsNullOrEmpty(s.Id))
                        {
                            s.FilePath = file;
                            _cache.Add(s);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[PersistenceKit] Skipping unreadable snapshot '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
                // Oldest first — UI flips for newest-first display.
                _cache.Sort((a, b) => string.Compare(a.CapturedAt, b.CapturedAt, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PersistenceKit] Failed to enumerate snapshot folder '{current}': {ex.Message}");
            }
        }

        private static void PersistOne(Snapshot snap)
        {
            var dir = FolderPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var fileName = $"{SanitiseLabel(snap.Label)}_{ToFileDate(snap.CapturedAt)}_{snap.Id.Substring(0, 8)}.json";
            snap.FilePath = Path.Combine(dir, fileName);

            try
            {
                var json = JsonConvert.SerializeObject(snap, Formatting.Indented);
                File.WriteAllText(snap.FilePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static string SanitiseLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "snapshot";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(label.Length);
            foreach (var c in label)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ') sb.Append('_');
                else sb.Append(c);
            }
            var result = sb.ToString().Trim('_');
            return result.Length == 0 ? "snapshot" : (result.Length > 40 ? result.Substring(0, 40) : result);
        }

        private static string ToFileDate(string iso)
        {
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        }
    }
}
