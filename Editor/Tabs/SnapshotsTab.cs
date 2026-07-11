using System;
using PersistenceKit.Editor.Snapshots;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PersistenceKit.Editor.Tabs
{
    /// <summary>
    /// Snapshot manager — capture every loaded state's persisted-field values under one
    /// label, restore later. Backed by <see cref="SnapshotStore"/> which persists to
    /// <c>Library/PersistenceKit/snapshots.json</c> (user-local, gitignored).
    /// </summary>
    internal sealed class SnapshotsTab
    {
        public VisualElement Root { get; }

        private readonly VisualElement _toolbar;
        private readonly TextField     _labelField;
        private readonly Button        _captureBtn;
        private readonly Label         _captureHint;
        private readonly VisualElement _list;

        // Only rebuild the list when the set of snapshots changes.
        private string _listSig;

        public SnapshotsTab()
        {
            Root = new VisualElement();
            Root.AddToClassList("la-body");
            Root.style.flexGrow = 1;

            var canvas = new VisualElement();
            canvas.AddToClassList("la-canvas");
            canvas.style.flexGrow = 1;

            canvas.Add(PersistenceKitWindow.Heading("Snapshots"));

            // Capture toolbar — label + Capture-the-world button.
            _toolbar = new VisualElement();
            _toolbar.AddToClassList("la-toolbar-row");

            _labelField = new TextField("Label");
            _labelField.style.flexGrow = 1;
            _toolbar.Add(_labelField);

            _captureBtn = new Button(OnCaptureClicked) { text = "Capture all" };
            _captureBtn.AddToClassList("la-toolbar__btn");
            _captureBtn.style.marginLeft = 6;
            _captureBtn.tooltip = "Capture every currently-loaded state's field values into one snapshot file.";
            _toolbar.Add(_captureBtn);

            var openFolderBtn = new Button(SnapshotStore.RevealFolderInFinder) { text = "Open Folder" };
            openFolderBtn.AddToClassList("la-toolbar__btn");
            openFolderBtn.style.marginLeft = 6;
            openFolderBtn.tooltip = "Open the snapshot folder in your OS file manager. Path is configurable in Project Settings → PersistenceKit.";
            _toolbar.Add(openFolderBtn);
            canvas.Add(_toolbar);

            _captureHint = new Label("0 loaded states will be captured.");
            _captureHint.AddToClassList("la-note");
            _captureHint.AddToClassList("la-note--indent");
            canvas.Add(_captureHint);

            canvas.Add(PersistenceKitWindow.Hint(
                "A snapshot freezes the whole world — every loaded state's persisted field values — under one label. " +
                "Each snapshot is its own .json file under the configured folder (Project Settings → PersistenceKit). " +
                "Restore writes the states back into their live instances and triggers a save."));

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            _list = new VisualElement();
            scroll.Add(_list);
            canvas.Add(scroll);

            Root.Add(canvas);
        }

        public void Refresh()
        {
            int loaded = 0;
            foreach (var m in PersistenceManager.ActiveManagers)
                loaded += m.SnapshotCache().Count;
            _captureBtn.SetEnabled(loaded > 0);
            _captureHint.text = loaded == 1 ? "1 loaded state will be captured." : $"{loaded} loaded states will be captured.";
            RebuildList();
        }

        private void OnCaptureClicked()
        {
            try
            {
                var snap = SnapshotStore.CaptureWorld(_labelField.value);
                Debug.Log($"[PersistenceKit] Snapshot '{snap.Label}' captured ({snap.StateCount} state(s), id {snap.Id.Substring(0, 8)})");
                _labelField.value = string.Empty;
                RebuildList();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Snapshot failed", ex.Message, "OK");
            }
        }

        private void RebuildList()
        {
            var snaps = SnapshotStore.All();

            // Skip the rebuild when the snapshot set is unchanged — otherwise the live-refresh
            // loop tears down every row several times a second.
            var sig = ComputeListSig(snaps);
            if (sig == _listSig) return;
            _listSig = sig;

            _list.Clear();
            if (snaps.Count == 0)
            {
                _list.Add(PersistenceKitWindow.Hint("No snapshots yet. Use Capture all to record the current world."));
                return;
            }

            // Newest first — captures are appended chronologically.
            for (int i = snaps.Count - 1; i >= 0; i--)
                _list.Add(BuildRow(snaps[i]));
        }

        private static string ComputeListSig(System.Collections.Generic.List<SnapshotStore.Snapshot> snaps)
        {
            var sb = new System.Text.StringBuilder(snaps.Count * 40);
            for (int i = 0; i < snaps.Count; i++)
                sb.Append(snaps[i].Id).Append('=').Append(snaps[i].Label).Append(';');
            return sb.ToString();
        }

        private VisualElement BuildRow(SnapshotStore.Snapshot snap)
        {
            var row = new VisualElement();
            row.AddToClassList("la-row");
            row.AddToClassList("la-snapshot-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;

            // Timestamp.
            var when = new Label(ParseTime(snap.CapturedAt));
            when.AddToClassList("la-snapshot-when");
            row.Add(when);

            // Label (bold) + state count (subtle).
            var labelCol = new VisualElement();
            labelCol.AddToClassList("la-snapshot-body");
            var labelLine = new Label(snap.Label);
            labelLine.AddToClassList("la-snapshot-label");
            labelLine.tooltip = snap.Label;
            labelCol.Add(labelLine);
            var sub = new Label($"{snap.StateCount} state(s)");
            sub.AddToClassList("la-snapshot-sub");
            labelCol.Add(sub);
            row.Add(labelCol);

            var restore = new Button(() => RestoreClicked(snap)) { text = "Restore" };
            restore.AddToClassList("la-toolbar__btn");
            restore.tooltip = "Write every state in this snapshot back into its live instance and trigger a save.";
            row.Add(restore);

            var reveal = PersistenceKitWindow.IconButton(
                () => SnapshotStore.RevealSnapshotInFinder(snap.Id),
                "→", "Reveal this snapshot file in your OS file manager.",
                "FolderOpened Icon", "Folder Icon", "Project");
            reveal.style.marginLeft = 4;
            row.Add(reveal);

            var del = PersistenceKitWindow.IconButton(
                () => DeleteClicked(snap),
                "×", "Delete this snapshot file.",   // U+00D7 multiplication sign (Latin-1, always renders)
                "TreeEditor.Trash", "d_TreeEditor.Trash");
            del.style.marginLeft = 4;
            row.Add(del);

            return row;
        }

        private async void RestoreClicked(SnapshotStore.Snapshot snap)
        {
            if (!EditorUtility.DisplayDialog(
                    "Restore snapshot",
                    $"Restore '{snap.Label}' — {snap.StateCount} state(s) — into the live world?\n\n" +
                    "Each state's fields will be overwritten and saved through every wired target. " +
                    "Encrypted fields are re-encrypted on save.",
                    "Restore", "Cancel"))
                return;

            try
            {
                var (restored, skipped) = await SnapshotStore.RestoreAsync(snap.Id);
                Debug.Log($"[PersistenceKit] Snapshot '{snap.Label}' restored — {restored} state(s) restored, {skipped} skipped.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Restore failed", ex.Message, "OK");
            }
        }

        private void DeleteClicked(SnapshotStore.Snapshot snap)
        {
            if (!EditorUtility.DisplayDialog(
                    "Delete snapshot",
                    $"Delete snapshot '{snap.Label}' captured at {ParseTime(snap.CapturedAt)}?",
                    "Delete", "Cancel"))
                return;

            if (SnapshotStore.Delete(snap.Id))
                RebuildList();
        }

        private static string ParseTime(string iso)
        {
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("MM/dd HH:mm");
            return iso;
        }
    }
}
