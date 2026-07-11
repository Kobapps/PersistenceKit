using System;
using System.Collections.Generic;
using System.IO;
using PersistenceKit.Editor.Skill;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PersistenceKit.Editor.Settings
{
    /// <summary>
    /// Registers the kit's settings page at <c>Edit → Project Settings → PersistenceKit</c>.
    /// Uses the same Kobapps stylesheet as the main window so the visuals stay consistent.
    /// </summary>
    internal static class PersistenceKitSettingsProvider
    {
        private const string SettingsRoot = "Project/PersistenceKit";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsRoot, SettingsScope.Project)
            {
                label = "PersistenceKit",
                keywords = new HashSet<string>(new[] {
                    "persistence", "save", "kit", "odin", "autosave", "encryption", "json", "binary"
                }),
                activateHandler = (search, root) => Build(root),
            };
        }

        private static void Build(VisualElement root)
        {
            // Reuse the window's stylesheet for consistent palette / chip / heading visuals.
            var uss = LoadStyleSheet();
            if (uss != null) root.styleSheets.Add(uss);

            var settings = PersistenceKitSettings.Instance;
            var ser = new SerializedObject(settings);

            // Scroll host — without this the page overflows (and clips) once the settings
            // window is shorter than the content.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            var page = new VisualElement();
            page.AddToClassList("la-root");
            page.style.paddingTop    = 8;
            page.style.paddingBottom = 8;
            page.style.paddingLeft   = 12;
            page.style.paddingRight  = 12;
            scroll.Add(page);

            page.Add(MakeHeading("Inspector"));
            var inspectorCard = MakeCard();
            inspectorCard.Add(MakeRendererField(ser));
            inspectorCard.Add(MakeIntField(ser, nameof(PersistenceKitSettings.AutoRefreshIntervalMs),
                "Refresh interval (ms)", "Live-refresh tick. Lower = snappier UI, more CPU."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.ShowTargetChips),
                "Show target chips", "Per-field colour-coded badges (Json / Binary / PlayerPrefs / Remote)."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.ShowDirtyChips),
                "Show dirty pills", "Tree rows surface unsaved-changes indicators."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.ShowFieldTypes),
                "Show field types", "Append the C# type name beside each field's label."));
            page.Add(inspectorCard);

            page.Add(MakeHeading("Activity"));
            var activityCard = MakeCard();
            activityCard.Add(MakeIntField(ser, nameof(PersistenceKitSettings.ActivityLogCapacity),
                "Log capacity", "Number of save events kept in the Activity tab's ring buffer."));
            page.Add(activityCard);

            page.Add(MakeHeading("Auto-save"));
            var autosaveCard = MakeCard();
            autosaveCard.Add(MakeFloatField(ser, nameof(PersistenceKitSettings.DefaultAutoSaveDebounceSeconds),
                "Default debounce (s)",
                "Used by AutoSaveLoop.Install() and the sample scene. Burst mutations within this window collapse to one save."));
            page.Add(autosaveCard);

            page.Add(MakeHeading("Snapshots"));
            var snapCard = MakeCard();
            snapCard.Add(MakeSnapshotsFolderField(ser));
            page.Add(snapCard);

            page.Add(MakeHeading("AI Assistant"));
            page.Add(MakeSkillCard());

            // Hint footer.
            var hint = new Label("Settings are stored in ProjectSettings/PersistenceKitSettings.asset and " +
                                 "saved when you change a value.");
            hint.AddToClassList("la-hint");
            page.Add(hint);

            // Persist on every change. Cheaper than tracking a Save button — Project Settings
            // pages don't have one by convention.
            page.RegisterCallback<ChangeEvent<int>>   (_ => Apply(ser));
            page.RegisterCallback<ChangeEvent<float>> (_ => Apply(ser));
            page.RegisterCallback<ChangeEvent<bool>>  (_ => Apply(ser));
            page.RegisterCallback<ChangeEvent<string>>(_ => Apply(ser));
            page.RegisterCallback<ChangeEvent<UnityEngine.Object>>(_ => Apply(ser));
            page.RegisterCallback<ChangeEvent<Enum>>  (_ => Apply(ser));
        }

        private static void Apply(SerializedObject ser)
        {
            ser.ApplyModifiedPropertiesWithoutUndo();
            PersistenceKitSettings.Save();
        }

        // ─── Widgets ─────────────────────────────────────────────

        private static VisualElement MakeHeading(string text)
        {
            var l = new Label(text.ToUpperInvariant());
            l.AddToClassList("la-heading");
            l.style.marginTop = 6;
            return l;
        }

        private static VisualElement MakeCard()
        {
            var c = new VisualElement();
            c.AddToClassList("la-card");
            return c;
        }

        private static VisualElement MakeRendererField(SerializedObject ser)
        {
            var prop = ser.FindProperty(nameof(PersistenceKitSettings.FieldRenderer));
            var field = new EnumField("Field renderer",
                (PersistenceKitSettings.InspectorRenderer)prop.intValue);
            field.tooltip = "Auto = use Odin if installed, else built-in. Default forces the built-in renderer.";
            field.RegisterValueChangedCallback(e =>
            {
                prop.intValue = (int)(PersistenceKitSettings.InspectorRenderer)e.newValue;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
            });
            field.style.marginBottom = 4;
            return field;
        }

        private static VisualElement MakeIntField(SerializedObject ser, string fieldName, string label, string tooltip)
        {
            var prop  = ser.FindProperty(fieldName);
            var field = new IntegerField(label) { value = prop.intValue, tooltip = tooltip };
            field.RegisterValueChangedCallback(e =>
            {
                prop.intValue = e.newValue;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
            });
            field.style.marginBottom = 4;
            return field;
        }

        private static VisualElement MakeFloatField(SerializedObject ser, string fieldName, string label, string tooltip)
        {
            var prop  = ser.FindProperty(fieldName);
            var field = new FloatField(label) { value = prop.floatValue, tooltip = tooltip };
            field.RegisterValueChangedCallback(e =>
            {
                prop.floatValue = e.newValue;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
            });
            field.style.marginBottom = 4;
            return field;
        }

        private static VisualElement MakeToggle(SerializedObject ser, string fieldName, string label, string tooltip)
        {
            var prop  = ser.FindProperty(fieldName);
            var field = new Toggle(label) { value = prop.boolValue, tooltip = tooltip };
            field.RegisterValueChangedCallback(e =>
            {
                prop.boolValue = e.newValue;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
            });
            field.style.marginBottom = 4;
            return field;
        }

        // ─── Snapshots folder (text + Browse + Reveal) ──────────

        private static VisualElement MakeSnapshotsFolderField(SerializedObject ser)
        {
            var prop = ser.FindProperty(nameof(PersistenceKitSettings.SnapshotsFolder));

            var col = new VisualElement();

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var field = new TextField("Folder") { value = prop.stringValue ?? string.Empty };
            field.tooltip = "Where snapshot .json files are written (one per snapshot). Relative paths resolve to " +
                            "the project root; absolute paths are used as-is. Default lives next to ProjectSettings/ " +
                            "rather than under Library/ so snapshots survive cache wipes.";
            field.style.flexGrow = 1;

            void Commit(string value)
            {
                prop.stringValue = value;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
                // The store caches its scanned folder — invalidate so the change takes effect.
                PersistenceKit.Editor.Snapshots.SnapshotStore.InvalidateCache();
            }

            field.RegisterValueChangedCallback(e => Commit(e.newValue));

            var browse = new Button(() =>
            {
                var start = ResolveSnapshotsAbsolute(prop.stringValue);
                var picked = EditorUtility.OpenFolderPanel("Snapshots folder", start, "");
                if (string.IsNullOrEmpty(picked)) return;

                // Store a project-relative path when the pick sits under the project root
                // (portable across machines); otherwise keep the absolute path.
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var full = Path.GetFullPath(picked);
                string stored = full;
                if (full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    stored = full.Substring(projectRoot.Length).TrimStart('/', '\\');
                    if (stored.Length == 0) stored = ".";
                }
                field.SetValueWithoutNotify(stored);
                Commit(stored);
            }) { text = "Browse…" };
            browse.AddToClassList("la-toolbar__btn");
            browse.style.marginLeft = 4;

            var reveal = new Button(PersistenceKit.Editor.Snapshots.SnapshotStore.RevealFolderInFinder) { text = "Reveal" };
            reveal.AddToClassList("la-toolbar__btn");
            reveal.style.marginLeft = 4;
            reveal.tooltip = "Open the current snapshots folder in your OS file manager (creates it if missing).";

            row.Add(field);
            row.Add(browse);
            row.Add(reveal);
            col.Add(row);
            return col;
        }

        private static string ResolveSnapshotsAbsolute(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) raw = "PersistenceKitSnapshots";
            if (Path.IsPathRooted(raw)) return raw;
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, raw));
        }

        // ─── AI skill installer card ─────────────────────────────

        private static VisualElement MakeSkillCard()
        {
            var card = MakeCard();

            var blurb = new Label(
                "Install the bundled \"persistencekit\" skill into this project's .claude/skills/ folder so AI " +
                "assistants (Claude Code / Agent SDK) know how to integrate PersistenceKit correctly — declaring " +
                "[PersistentState] classes, wiring the builder, saving/loading, encryption, and verifying via the " +
                "Unity MCP.");
            blurb.AddToClassList("la-hint");
            blurb.style.whiteSpace = WhiteSpace.Normal;
            blurb.style.marginBottom = 6;
            card.Add(blurb);

            var status = new Label();
            status.AddToClassList("la-note");
            status.style.marginBottom = 6;
            status.style.whiteSpace = WhiteSpace.Normal;
            card.Add(status);

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.alignItems = Align.Center;

            var install = new Button { text = "Install Claude Skill" };
            install.AddToClassList("la-toolbar__btn");

            var reveal = new Button { text = "Reveal" };
            reveal.AddToClassList("la-toolbar__btn");
            reveal.style.marginLeft = 4;

            void RefreshStatus()
            {
                bool installed = PersistenceKitSkillInstaller.IsInstalled();
                status.text = installed
                    ? $"Installed → {ToProjectRelative(PersistenceKitSkillInstaller.DestinationDir)}"
                    : "Not installed.";
                install.text = installed ? "Reinstall / Update" : "Install Claude Skill";
                reveal.SetEnabled(installed);
            }

            install.clicked += () =>
            {
                try
                {
                    bool exists = PersistenceKitSkillInstaller.IsInstalled();
                    if (exists && !EditorUtility.DisplayDialog(
                            "Reinstall skill",
                            "The persistencekit skill is already installed. Overwrite it with the version bundled " +
                            "in this package?",
                            "Overwrite", "Cancel"))
                        return;

                    var dest = PersistenceKitSkillInstaller.Install(overwrite: true);
                    RefreshStatus();
                    Debug.Log($"[PersistenceKit] Installed AI skill to {dest}");
                    EditorUtility.DisplayDialog(
                        "Skill installed",
                        $"The persistencekit skill is now at:\n\n{dest}\n\n" +
                        "Restart your AI assistant / reindex skills if it doesn't pick it up automatically.",
                        "OK");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    EditorUtility.DisplayDialog("Install failed", ex.Message, "OK");
                }
            };

            reveal.clicked += () =>
            {
                var dir = PersistenceKitSkillInstaller.DestinationDir;
                if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir);
            };

            buttonRow.Add(install);
            buttonRow.Add(reveal);
            card.Add(buttonRow);

            RefreshStatus();
            return card;
        }

        private static string ToProjectRelative(string absolute)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var full = Path.GetFullPath(absolute);
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(projectRoot.Length).TrimStart('/', '\\')
                : full;
        }

        private static StyleSheet LoadStyleSheet()
        {
            var guids = AssetDatabase.FindAssets("PersistenceKitWindow t:StyleSheet");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }
}
