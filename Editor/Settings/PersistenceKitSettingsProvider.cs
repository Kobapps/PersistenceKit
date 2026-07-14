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
                    "persistence", "save", "kit", "odin", "autosave", "encryption", "json", "binary",
                    "edit mode", "saved data", "slot", "storage", "root", "key"
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
                "Show dirty dots", "Light up a tree row's per-target dot when that store has unsaved writes."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.ShowFieldTypes),
                "Show field types", "Append the C# type name beside each field's label."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.GroupFieldsByTarget),
                "Group fields by target", "Sort the field grid under a header per target instead of one flat list."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.HighlightChangedFields),
                "Highlight changed fields", "Tint fields whose value differs from a freshly-constructed instance."));
            inspectorCard.Add(MakeToggle(ser, nameof(PersistenceKitSettings.ShowOverview),
                "Show overview strip", "Summary counters (states / types / unsaved / bytes) above the field grid."));
            page.Add(inspectorCard);

            page.Add(MakeHeading("Edit Mode"));
            page.Add(MakeEditModeCard(ser));

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

        /// <summary>
        /// Drop the edit-mode session so the next load rebuilds against the new settings.
        /// </summary>
        /// <remarks>
        /// The session captures its targets, default target and encryptor at Build() time.
        /// Without this, changing the key or a storage root here leaves the window showing data
        /// read with the old wiring and no hint that the setting hasn't taken effect.
        /// </remarks>
        private static void InvalidateEditModeSession()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditModeSession.Stop();
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

        // ─── Edit-mode session ───────────────────────────────────

        /// <summary>
        /// Wiring for the manager the window builds when the game isn't running. The editor
        /// can't see the game's <c>PersistenceKitBuilder</c> — it lives in user code that only
        /// runs at startup — so these have to be told, and a mismatch means the window reads a
        /// different store than the game writes.
        /// </summary>
        private static VisualElement MakeEditModeCard(SerializedObject ser)
        {
            var card = MakeCard();

            var blurb = new Label(
                "Outside Play mode the window builds its own manager so you can read, edit and reset saved data " +
                "without entering Play mode. It can't see your PersistenceKitBuilder, so these must mirror it — " +
                "otherwise you'll be looking at the wrong store.");
            blurb.AddToClassList("la-hint");
            blurb.style.whiteSpace = WhiteSpace.Normal;
            blurb.style.marginBottom = 6;
            card.Add(blurb);

            card.Add(MakeToggle(ser, nameof(PersistenceKitSettings.EditModeAutoLoad),
                "Load on window open",
                "Read saved states as soon as the window opens outside Play mode. Off = press 'Load Saved States' yourself."));

            var defaultProp = ser.FindProperty(nameof(PersistenceKitSettings.EditModeDefaultTarget));
            var defaultField = new EnumField("Default target", (PersistTarget)defaultProp.intValue)
            {
                tooltip = "Where bare [Persist] fields go. Must match PersistenceKitBuilder.UseDefaultTarget(...).",
            };
            defaultField.RegisterValueChangedCallback(e =>
            {
                defaultProp.intValue = (int)(PersistTarget)e.newValue;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
                InvalidateEditModeSession();
            });
            defaultField.style.marginBottom = 4;
            card.Add(defaultField);

            card.Add(MakeRootField(ser, nameof(PersistenceKitSettings.EditModeJsonRoot), "JSON root", "json"));
            card.Add(MakeRootField(ser, nameof(PersistenceKitSettings.EditModeBinaryRoot), "Binary root", "binary"));

            card.Add(MakeEncryptionKeyField(ser));
            return card;
        }

        /// <summary>
        /// The AES key the edit-mode session decrypts and encrypts with. Generating one here is
        /// a convenience for projects that don't have a key yet — it deliberately does NOT
        /// happen automatically, because writing a save with a key the game doesn't share would
        /// produce a file the game can't read.
        /// </summary>
        private static VisualElement MakeEncryptionKeyField(SerializedObject ser)
        {
            var prop = ser.FindProperty(nameof(PersistenceKitSettings.EditModeEncryptionKey));

            var col = new VisualElement();

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var field = new TextField("Encryption key (Base64)") { value = prop.stringValue ?? string.Empty };
            field.tooltip = "Base64 of the 32-byte AES key. Must be the same key your game passes to " +
                            "ConstantKeyProvider, or the window reads and writes gibberish. " +
                            "Leave empty if you don't use [Encrypted].";
            field.style.flexGrow = 1;

            var status = new Label();
            status.AddToClassList("la-note");
            status.style.whiteSpace = WhiteSpace.Normal;

            void Validate(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    status.text = "No key set — states with [Encrypted] fields can't be opened or saved outside Play mode.";
                    return;
                }
                try
                {
                    int len = Convert.FromBase64String(value.Trim()).Length;
                    status.text = len == 32
                        ? "Key looks valid (32 bytes)."
                        : $"Key decodes to {len} bytes; AES-256 needs exactly 32. It will be ignored.";
                }
                catch
                {
                    status.text = "Not valid Base64 — the key will be ignored.";
                }
            }

            void Commit(string value)
            {
                prop.stringValue = value;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
                Validate(value);
                InvalidateEditModeSession();
            }

            field.RegisterValueChangedCallback(e => Commit(e.newValue));

            var generate = new Button(() =>
            {
                if (!string.IsNullOrWhiteSpace(prop.stringValue)
                    && !EditorUtility.DisplayDialog(
                        "Replace encryption key",
                        "A key is already set. Replacing it means any save already written with the old key " +
                        "can no longer be decrypted here.\n\nReplace it?",
                        "Replace", "Cancel"))
                    return;

                var key = PersistenceKit.Editor.EditModeSession.GenerateKeyBase64();
                field.SetValueWithoutNotify(key);
                Commit(key);
                Debug.Log("[PersistenceKit] Generated a new 32-byte edit-mode key. Pass the same bytes to your " +
                          "game's ConstantKeyProvider or the two won't be able to read each other's saves.");
            }) { text = "Generate" };
            generate.AddToClassList("la-toolbar__btn");
            generate.style.marginLeft = 4;
            generate.tooltip = "Create a random 32-byte key. Only useful if your game uses this same key.";

            row.Add(field);
            row.Add(generate);
            col.Add(row);
            col.Add(status);

            var keyNote = new Label(
                "Stored as plain text in ProjectSettings/PersistenceKitSettings.asset, which travels with your repo. " +
                "Use a development key here — not the one that ships.");
            keyNote.AddToClassList("la-note");
            keyNote.style.whiteSpace = WhiteSpace.Normal;
            col.Add(keyNote);

            Validate(prop.stringValue);
            return col;
        }

        /// <summary>Storage-root text field with a Browse picker; empty means "the target's own default".</summary>
        private static VisualElement MakeRootField(SerializedObject ser, string fieldName, string label, string defaultLeaf)
        {
            var prop = ser.FindProperty(fieldName);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;

            var field = new TextField(label) { value = prop.stringValue ?? string.Empty };
            field.tooltip = $"Empty = <persistentDataPath>/PersistenceKit/{defaultLeaf}, which is what the target's " +
                            "default constructor uses. Relative paths resolve to the project root.";
            field.style.flexGrow = 1;

            void Commit(string value)
            {
                prop.stringValue = value;
                ser.ApplyModifiedPropertiesWithoutUndo();
                PersistenceKitSettings.Save();
                InvalidateEditModeSession();
            }
            field.RegisterValueChangedCallback(e => Commit(e.newValue));

            var browse = new Button(() =>
            {
                var start = string.IsNullOrWhiteSpace(prop.stringValue)
                    ? Path.Combine(Application.persistentDataPath, "PersistenceKit", defaultLeaf)
                    : prop.stringValue;
                var picked = EditorUtility.OpenFolderPanel(label, start, "");
                if (string.IsNullOrEmpty(picked)) return;
                field.SetValueWithoutNotify(picked);
                Commit(picked);
            }) { text = "Browse…" };
            browse.AddToClassList("la-toolbar__btn");
            browse.style.marginLeft = 4;

            var reveal = new Button(() =>
            {
                var dir = string.IsNullOrWhiteSpace(prop.stringValue)
                    ? Path.Combine(Application.persistentDataPath, "PersistenceKit", defaultLeaf)
                    : Path.GetFullPath(prop.stringValue);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                EditorUtility.RevealInFinder(dir);
            }) { text = "Reveal" };
            reveal.AddToClassList("la-toolbar__btn");
            reveal.style.marginLeft = 4;
            reveal.tooltip = "Open this target's folder in your OS file manager (creates it if missing).";

            row.Add(field);
            row.Add(browse);
            row.Add(reveal);
            return row;
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
