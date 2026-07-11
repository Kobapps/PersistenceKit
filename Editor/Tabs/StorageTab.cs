using System;
using System.Collections.Generic;
using System.IO;
using PersistenceKit.Targets;
using UnityEditor;
using UnityEngine.UIElements;

namespace PersistenceKit.Editor.Tabs
{
    /// <summary>
    /// Browse the bytes that wound up on disk / in PlayerPrefs / in the remote stub. Pure
    /// view of the current storage; no edits beyond Delete and Reveal-in-Finder.
    /// </summary>
    internal sealed class StorageTab
    {
        public VisualElement Root { get; }

        private readonly VisualElement _targetList;
        private readonly Label         _selectionTitle;
        private readonly VisualElement _toolbar;
        private readonly Button        _revealFolderBtn;
        private readonly VisualElement _entryList;
        private readonly TextField     _preview;

        private (PersistenceManager manager, IPersistenceTarget target)? _selection;
        private string _previewedKey;     // sticky across live-refresh ticks

        // Change-detection caches. The live-refresh loop calls Refresh() several times a
        // second; without these the target list + entry list were fully rebuilt every tick
        // (losing hover/selection, and — worse — re-running disk enumeration and a blocking
        // sync-over-async read on the editor UI thread every ~330 ms).
        private string _targetStructureSig;
        private readonly List<(VisualElement row, PersistenceManager m, IPersistenceTarget t)> _targetRows
            = new List<(VisualElement, PersistenceManager, IPersistenceTarget)>();
        private string _entriesSig;
        private readonly List<(VisualElement row, string key)> _entryRows
            = new List<(VisualElement, string)>();

        public StorageTab()
        {
            Root = new VisualElement();
            Root.AddToClassList("la-body");
            Root.style.flexGrow = 1;

            // Left sidebar: targets.
            var left = new VisualElement();
            left.AddToClassList("la-sidebar");
            left.Add(PersistenceKitWindow.Heading("Targets"));
            var leftScroll = new ScrollView(ScrollViewMode.Vertical);
            _targetList = new VisualElement();
            leftScroll.Add(_targetList);
            left.Add(leftScroll);
            Root.Add(left);

            // Canvas: entry list + preview.
            var canvas = new VisualElement();
            canvas.AddToClassList("la-canvas");
            _selectionTitle = new Label("Pick a target on the left");
            _selectionTitle.AddToClassList("la-heading");
            canvas.Add(_selectionTitle);

            // Toolbar — actions that operate on the currently-selected target.
            _toolbar = new VisualElement();
            _toolbar.AddToClassList("la-toolbar-row");
            _revealFolderBtn = new Button(RevealSelectedTargetFolder) { text = "Reveal Folder" };
            _revealFolderBtn.AddToClassList("la-toolbar__btn");
            _revealFolderBtn.tooltip = "Open the target's storage directory in your OS file manager.";
            _revealFolderBtn.style.display = DisplayStyle.None;     // shown only for disk targets
            _toolbar.Add(_revealFolderBtn);
            canvas.Add(_toolbar);

            var entryScroll = new ScrollView(ScrollViewMode.Vertical);
            entryScroll.style.maxHeight = 220;
            _entryList = new VisualElement();
            entryScroll.Add(_entryList);
            canvas.Add(entryScroll);

            _preview = new TextField { multiline = true };
            _preview.SetEnabled(false);
            _preview.AddToClassList("la-preview");
            canvas.Add(_preview);

            Root.Add(canvas);
        }

        public void Refresh()
        {
            RebuildTargetListIfChanged();
            UpdateTargetSelectionClasses();
            RebuildEntriesIfChanged();
            UpdateEntrySelectionClasses();
        }

        // ─── Target list ─────────────────────────────────────────

        private void RebuildTargetListIfChanged()
        {
            var managers = PersistenceManager.ActiveManagers;
            var sig = ComputeTargetStructureSig(managers);
            if (sig == _targetStructureSig) return;      // structure unchanged — keep the rows
            _targetStructureSig = sig;

            _targetList.Clear();
            _targetRows.Clear();

            if (managers.Count == 0)
            {
                _targetList.Add(PersistenceKitWindow.Hint("No active manager — no storage to browse."));
                return;
            }

            for (int mi = 0; mi < managers.Count; mi++)
            {
                var m = managers[mi];
                var heading = new Label($"Manager #{mi + 1}");
                heading.AddToClassList("la-heading");
                heading.AddToClassList("la-heading--sub");
                _targetList.Add(heading);

                foreach (var kv in m.Options.Targets)
                {
                    var row = new VisualElement();
                    row.AddToClassList("la-row");
                    var label = new Label(kv.Key.ToString());
                    label.AddToClassList("la-row__label");
                    row.Add(label);
                    row.Add(PersistenceKitWindow.Chip(kv.Key.ToString(), PersistenceKitWindow.TargetChipClass(kv.Key)));

                    var capturedM = m;
                    var capturedT = kv.Value;
                    row.RegisterCallback<MouseUpEvent>(_ =>
                    {
                        _selection = (capturedM, capturedT);
                        Refresh();
                    });
                    _targetList.Add(row);
                    _targetRows.Add((row, m, kv.Value));
                }
            }
        }

        private void UpdateTargetSelectionClasses()
        {
            foreach (var (row, m, t) in _targetRows)
            {
                bool selected = _selection.HasValue
                    && ReferenceEquals(_selection.Value.manager, m)
                    && ReferenceEquals(_selection.Value.target, t);
                row.EnableInClassList("la-row--selected", selected);
            }
        }

        private static string ComputeTargetStructureSig(List<PersistenceManager> managers)
        {
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < managers.Count; i++)
            {
                sb.Append(managers[i].GetHashCode()).Append(':');
                foreach (var kv in managers[i].Options.Targets) sb.Append(kv.Key).Append(',');
                sb.Append(';');
            }
            return sb.ToString();
        }

        // ─── Entry list ──────────────────────────────────────────

        private void RebuildEntriesIfChanged()
        {
            // NB: _preview is intentionally NOT cleared here — the live-refresh tick would
            // otherwise erase the user's current selection mid-read. The preview is only
            // updated when the user clicks an entry (PreviewEntry below).

            if (!_selection.HasValue)
            {
                if (_entriesSig != "<none>")
                {
                    _entriesSig = "<none>";
                    _entryList.Clear();
                    _entryRows.Clear();
                    _selectionTitle.text = "PICK A TARGET ON THE LEFT";
                    _revealFolderBtn.style.display = DisplayStyle.None;
                }
                return;
            }

            var (manager, target) = _selection.Value;
            var entries = EnumerateEntries(manager, target);

            // Signature captures the selected target + the set of (key, size) pairs, so we
            // only touch the visual tree when the on-disk / in-store contents actually change.
            var sig = ComputeEntriesSig(target, entries);
            if (sig == _entriesSig)
            {
                _selectionTitle.text = $"{target.Target}    {DescribeStorage(target)}";
                return;
            }
            _entriesSig = sig;

            _entryList.Clear();
            _entryRows.Clear();
            _selectionTitle.text = $"{target.Target}    {DescribeStorage(target)}";

            bool isDisk = target is JsonDiskTarget || target is BinaryDiskTarget;
            _revealFolderBtn.style.display = isDisk ? DisplayStyle.Flex : DisplayStyle.None;

            if (entries.Count == 0)
            {
                _entryList.Add(PersistenceKitWindow.Hint(
                    "No entries found. Disk targets list files in their root directory; PlayerPrefs/Remote can only enumerate keys we know about (drawn from cached states)."));
                return;
            }

            foreach (var e in entries)
            {
                var row = new VisualElement();
                row.AddToClassList("la-storage-row");

                var name = new Label(e.Key);
                name.AddToClassList("la-storage-row__name");
                name.tooltip = e.Key;
                row.Add(name);

                var size = new Label(PersistenceKitWindow.FormatBytes(e.SizeBytes));
                size.AddToClassList("la-storage-row__size");
                row.Add(size);

                // Per-row reveal — only meaningful for disk targets where a real file exists.
                if (isDisk)
                {
                    var capturedKeyInner = e.Key;
                    var capturedTargetInner = target;
                    var revealBtn = PersistenceKitWindow.IconButton(
                        () => RevealEntryFile(capturedTargetInner, capturedKeyInner),
                        "→", "Reveal this file in your OS file manager.",
                        "FolderOpened Icon", "Folder Icon", "Project");
                    revealBtn.style.marginLeft = 4;
                    // Don't let the reveal click bubble into the row's preview handler.
                    revealBtn.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
                    row.Add(revealBtn);
                }

                var capturedKey = e.Key;
                var capturedTarget = target;
                row.RegisterCallback<MouseUpEvent>(_ => PreviewEntry(capturedTarget, capturedKey));

                _entryList.Add(row);
                _entryRows.Add((row, e.Key));
            }
        }

        private void UpdateEntrySelectionClasses()
        {
            foreach (var (row, key) in _entryRows)
                row.EnableInClassList("la-row--selected", key == _previewedKey);
        }

        private static string ComputeEntriesSig(IPersistenceTarget target, List<StorageEntry> entries)
        {
            var sb = new System.Text.StringBuilder(64);
            sb.Append(target.GetHashCode()).Append('|');
            for (int i = 0; i < entries.Count; i++)
                sb.Append(entries[i].Key).Append('=').Append(entries[i].SizeBytes).Append(';');
            return sb.ToString();
        }

        // ─── Reveal-in-Explorer helpers ──────────────────────────

        private void RevealSelectedTargetFolder()
        {
            if (!_selection.HasValue) return;
            var target = _selection.Value.target;
            string root =
                target is JsonDiskTarget j   ? j.RootDirectory :
                target is BinaryDiskTarget b ? b.RootDirectory : null;
            if (root == null) return;

            // EditorUtility.RevealInFinder works on Windows (Explorer), macOS (Finder), and
            // Linux (xdg-open). Create the directory if a save hasn't run yet so the OS has
            // somewhere to open.
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            EditorUtility.RevealInFinder(root);
        }

        private static void RevealEntryFile(IPersistenceTarget target, string key)
        {
            string root, ext;
            switch (target)
            {
                case JsonDiskTarget j:    root = j.RootDirectory; ext = ".json"; break;
                case BinaryDiskTarget b:  root = b.RootDirectory; ext = ".bin";  break;
                default: return;
            }

            // Match the disk target's filename sanitisation (slot separator ':' → '_').
            var safeKey = SanitizeForFile(key);
            var path = Path.Combine(root, safeKey + ext);
            if (File.Exists(path))
                EditorUtility.RevealInFinder(path);
            else if (Directory.Exists(root))
                EditorUtility.RevealInFinder(root);
        }

        private static string SanitizeForFile(string key)
        {
            // Mirror DiskTargetBase.SanitizeKey — replaces ':' / path separators with '_'.
            var invalid = Path.GetInvalidFileNameChars();
            var chars = key.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c == ':' || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                {
                    chars[i] = '_';
                    continue;
                }
                for (int j = 0; j < invalid.Length; j++)
                    if (c == invalid[j]) { chars[i] = '_'; break; }
            }
            return new string(chars);
        }

        private async void PreviewEntry(IPersistenceTarget target, string key)
        {
            _previewedKey = key;
            UpdateEntrySelectionClasses();   // reflect the click immediately, before the async load
            try
            {
                var bytes = await target.LoadAsync(key, default);
                if (bytes == null) { _preview.value = "<missing>"; return; }
                _preview.value = TryDecodeText(bytes) ?? $"[{bytes.Length} bytes — non-text payload]";
            }
            catch (Exception ex)
            {
                _preview.value = "Error loading entry: " + ex.Message;
            }
        }

        private static string TryDecodeText(byte[] bytes)
        {
            try
            {
                var s = System.Text.Encoding.UTF8.GetString(bytes);
                // Heuristic: presence of typical control bytes signals binary.
                int controls = 0;
                for (int i = 0; i < s.Length && i < 256; i++)
                {
                    var c = s[i];
                    if (c < 32 && c != '\n' && c != '\r' && c != '\t') controls++;
                }
                if (controls > 4) return null;
                return s;
            }
            catch { return null; }
        }

        private static string DescribeStorage(IPersistenceTarget target)
        {
            switch (target)
            {
                case JsonDiskTarget j:    return j.RootDirectory;
                case BinaryDiskTarget b:  return b.RootDirectory;
                case PlayerPrefsTarget _: return "PlayerPrefs (prefix \"pk:\")";
                case RemoteTarget _:      return "remote provider (in-memory or user-supplied)";
                default:                  return target.GetType().Name;
            }
        }

        private struct StorageEntry { public string Key; public long SizeBytes; }

        private static List<StorageEntry> EnumerateEntries(PersistenceManager manager, IPersistenceTarget target)
        {
            var list = new List<StorageEntry>();
            switch (target)
            {
                case JsonDiskTarget _:
                case BinaryDiskTarget _:
                {
                    var root = (target is JsonDiskTarget j) ? j.RootDirectory : ((BinaryDiskTarget)target).RootDirectory;
                    var ext  = (target is JsonDiskTarget) ? ".json" : ".bin";
                    if (!Directory.Exists(root)) break;
                    foreach (var path in Directory.EnumerateFiles(root, "*" + ext, SearchOption.TopDirectoryOnly))
                    {
                        var info = new FileInfo(path);
                        list.Add(new StorageEntry { Key = Path.GetFileNameWithoutExtension(path), SizeBytes = info.Length });
                    }
                    break;
                }
                case PlayerPrefsTarget _:
                case RemoteTarget _:
                {
                    // Neither store has an enumerate API, so we project the keys we know exist
                    // (the cached states' keys) and check Exists(). Sizes are read only when
                    // the target's LoadAsync completes synchronously — never blocking the
                    // editor UI thread. In-memory / PlayerPrefs stores complete inline; a
                    // genuinely async custom provider simply reports size 0 here.
                    foreach (var s in manager.SnapshotCache())
                    {
                        if (!target.Exists(s.Key)) continue;
                        list.Add(new StorageEntry { Key = s.Key, SizeBytes = TryReadSizeNonBlocking(target, s.Key) });
                    }
                    break;
                }
            }
            return list;
        }

        /// <summary>
        /// Reads a payload's byte length only if the target's <c>LoadAsync</c> has already
        /// completed synchronously. Returns 0 rather than blocking the UI thread on a
        /// still-running task — the old code called <c>Task.Wait(50)</c> here every refresh
        /// tick, which froze the editor when a target's read was slow.
        /// </summary>
        private static long TryReadSizeNonBlocking(IPersistenceTarget target, string key)
        {
            try
            {
                var vt = target.LoadAsync(key, default);
                if (!vt.IsCompleted) return 0;      // don't block — size is cosmetic
                var bytes = vt.Result;              // safe: completed synchronously
                return bytes?.Length ?? 0;
            }
            catch { return 0; }
        }
    }
}
