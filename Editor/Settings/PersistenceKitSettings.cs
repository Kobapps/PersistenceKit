using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace PersistenceKit.Editor.Settings
{
    /// <summary>
    /// Project-scoped editor settings for the kit. Persisted to
    /// <c>ProjectSettings/PersistenceKitSettings.asset</c> via
    /// <see cref="InternalEditorUtility.SaveToSerializedFileAndForget"/> so the file
    /// doesn't pollute <c>Assets/</c> and rides along with the project's source control.
    /// </summary>
    public sealed class PersistenceKitSettings : ScriptableObject
    {
        public enum InspectorRenderer
        {
            /// <summary>Use Odin's <c>PropertyTree</c> when ODIN_INSPECTOR is defined; fall back to the built-in renderer otherwise.</summary>
            Auto    = 0,
            /// <summary>Force Odin (no-op when Odin isn't installed — falls back to Auto behaviour).</summary>
            Odin    = 1,
            /// <summary>Force the built-in renderer (SerializeReference + reflection drawer) even when Odin is available.</summary>
            Default = 2,
        }

        // ─── Inspector window ─────────────────────────────────────
        [Tooltip("Which renderer to use for complex field values in the Inspector tab.")]
        public InspectorRenderer FieldRenderer = InspectorRenderer.Auto;

        [Tooltip("Editor window live-refresh interval. Lower = more responsive but more CPU.")]
        [Range(100, 2000)]
        public int AutoRefreshIntervalMs = 330;

        [Tooltip("Hide / show the colour-coded target chip on each field row.")]
        public bool ShowTargetChips = true;

        [Tooltip("Hide / show the dirty pill on each state in the tree.")]
        public bool ShowDirtyChips = true;

        [Tooltip("Show the field's C# type beside its name.")]
        public bool ShowFieldTypes = false;

        // ─── Activity log ─────────────────────────────────────────
        [Tooltip("Size of the in-memory ring buffer that backs the Activity tab.")]
        [Range(64, 4096)]
        public int ActivityLogCapacity = 512;

        // ─── Auto-save defaults ───────────────────────────────────
        [Tooltip("Debounce duration the sample scene + AutoSaveLoop.Install use by default.")]
        [Range(0.05f, 5f)]
        public float DefaultAutoSaveDebounceSeconds = 0.5f;

        // ─── Snapshots ────────────────────────────────────────────
        [Tooltip("Folder where snapshot JSON files are written (one .json per snapshot). " +
                 "Relative paths resolve to the project root. Default lives next to ProjectSettings/ " +
                 "rather than under Library/ so snapshots survive cache wipes and test runs.")]
        public string SnapshotsFolder = "PersistenceKitSnapshots";

        // ─── Persistence ──────────────────────────────────────────
        private const string SettingsPath = "ProjectSettings/PersistenceKitSettings.asset";

        private static PersistenceKitSettings _instance;
        public  static PersistenceKitSettings Instance => _instance ??= LoadOrCreate();

        public static SerializedObject GetSerializedSettings()
            => new SerializedObject(Instance);

        public static void Save()
        {
            if (_instance == null) return;
            try
            {
                InternalEditorUtility.SaveToSerializedFileAndForget(
                    new UnityEngine.Object[] { _instance }, SettingsPath, allowTextSerialization: true);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static PersistenceKitSettings LoadOrCreate()
        {
            try
            {
                var loaded = InternalEditorUtility.LoadSerializedFileAndForget(SettingsPath);
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Length; i++)
                        if (loaded[i] is PersistenceKitSettings s) return s;
                }
            }
            catch (Exception ex) { Debug.LogException(ex); }

            var fresh = CreateInstance<PersistenceKitSettings>();
            fresh.hideFlags = HideFlags.HideAndDontSave;
            return fresh;
        }
    }
}
