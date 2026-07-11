using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PersistenceKit.Autosave;
using PersistenceKit.Editor.Settings;
using PersistenceKit.Editor.Snapshots;
using PersistenceKit.Editor.Tabs;
using PersistenceKit.Editor.Tools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PersistenceKit.Editor
{
    /// <summary>
    /// "PersistenceKit" editor window — manage and monitor live state, browse on-disk
    /// storage, watch the save activity log. UI Toolkit; styled with the Kobapps design
    /// system (see PersistenceKitWindow.uss).
    /// </summary>
    public sealed class PersistenceKitWindow : EditorWindow
    {
        private const string MenuPath = "Window/PersistenceKit/Inspector";
        private const string UssGuidProbeName = "PersistenceKitWindow.uss";

        private InspectorTab _inspector;
        private StorageTab   _storage;
        private ActivityTab  _activity;
        private SnapshotsTab _snapshots;

        private ActivityLog _log;
        private readonly HashSet<PersistenceManager> _subscribed = new HashSet<PersistenceManager>();

        // Per-(state, field) snapshots used to diff field values across save events. The
        // snapshot value is a stable string form (JSON for complex types, ToString for
        // primitives) so collection mutations are detected even when the reference doesn't
        // change. Filled in lazily during LiveRefresh and updated after each save's diff.
        private readonly Dictionary<(IPersistentState state, string field), string> _fieldSnapshots
            = new Dictionary<(IPersistentState, string), string>();
        private readonly HashSet<IPersistentState> _snapshottedStates = new HashSet<IPersistentState>();

        // True while an import is in flight. The kit fires one OnSaved per imported state;
        // logging each one would drown the Activity tab in per-field diffs that duplicate
        // the import file. We still consume the events to keep snapshots fresh.
        private bool _importInFlight;

        // Same idea for snapshot restore — one Save fires per restored state, and the
        // single Restore summary entry already covers what happened.
        private bool _snapshotRestoreInFlight;

        // And again for Reset All. Each reset state fires Save; we suppress the per-state
        // log rows in favour of one summary entry at the end.
        private bool _resetAllInFlight;

        private VisualElement _tabRow;
        private VisualElement _body;
        private Label _statusLeft;
        private Label _statusRight;

        // Sync widget bits.
        private VisualElement _syncDot;
        private Label         _syncLabel;
        private Button        _pauseResumeBtn;
        private Button        _stopStartBtn;

        private string _activeTab = "inspector";
        private double _lastRefreshTime;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var w = GetWindow<PersistenceKitWindow>("PersistenceKit");
            w.minSize = new Vector2(720, 360);
            w.Show();
        }

        private void CreateGUI()
        {
            var settings = PersistenceKitSettings.Instance;
            _log = new ActivityLog(capacity: Mathf.Max(64, settings.ActivityLogCapacity));

            var root = rootVisualElement;
            root.AddToClassList("la-root");

            var uss = LoadStyleSheet();
            if (uss != null)
            {
                root.styleSheets.Add(uss);
            }
            else
            {
                // Without the stylesheet the window renders as unstyled default controls,
                // which reads as "broken". Say so plainly rather than leaving the user to
                // guess why the layout looks wrong.
                var warn = new Label("PersistenceKitWindow.uss could not be located — the window is showing unstyled. " +
                                     "Reimport the PersistenceKit/Editor folder.");
                warn.AddToClassList("la-fallback-warning");
                warn.style.color = new StyleColor(new Color(1f, 0.42f, 0.42f));
                warn.style.whiteSpace = WhiteSpace.Normal;
                warn.style.paddingLeft = 8;
                warn.style.paddingRight = 8;
                warn.style.paddingTop = 8;
                warn.style.paddingBottom = 8;
                root.Add(warn);
            }

            BuildTabRow(root);
            BuildBody(root);
            BuildStatusBar(root);

            SelectTab("inspector");
            RefreshAll();

            // Live refresh tick — interval driven by Project Settings (default ~3 Hz).
            int interval = Mathf.Clamp(settings.AutoRefreshIntervalMs, 100, 2000);
            root.schedule.Execute(LiveRefresh).Every(interval);

            // Push snapshot capture/restore events into the activity log.
            SnapshotStore.OnCaptured  += OnSnapshotCaptured;
            SnapshotStore.OnRestoring += OnSnapshotRestoring;
            SnapshotStore.OnRestored  += OnSnapshotRestored;
        }

        private void OnDisable()
        {
            UnsubscribeAll();
            _inspector?.Dispose();
            SnapshotStore.OnCaptured  -= OnSnapshotCaptured;
            SnapshotStore.OnRestoring -= OnSnapshotRestoring;
            SnapshotStore.OnRestored  -= OnSnapshotRestored;
        }

        private void OnSnapshotRestoring(SnapshotStore.Snapshot _) => _snapshotRestoreInFlight = true;

        private void OnSnapshotCaptured(SnapshotStore.Snapshot snap)
        {
            int bytes = FileSize(snap.FilePath);
            _log?.Push(ActivityLog.Entry.ForSnapshotCapture(
                DateTime.UtcNow,
                snap.Label,
                bytes,
                $"{snap.StateCount} state(s) → {(string.IsNullOrEmpty(snap.FilePath) ? snap.Label : System.IO.Path.GetFileName(snap.FilePath))}"));
            if (_activeTab == "activity") _activity?.Refresh();
        }

        private void OnSnapshotRestored(SnapshotStore.Snapshot snap, int restored, int skipped)
        {
            _snapshotRestoreInFlight = false;
            var summary = skipped > 0
                ? $"{restored} restored, {skipped} skipped ← {snap.Label}"
                : $"{restored} state(s) ← {snap.Label}";
            _log?.Push(ActivityLog.Entry.ForSnapshotRestore(
                DateTime.UtcNow,
                snap.Label,
                FileSize(snap.FilePath),
                summary));
            if (_activeTab == "activity") _activity?.Refresh();
        }

        private static int FileSize(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return 0;
                return (int)Math.Min(int.MaxValue, new System.IO.FileInfo(path).Length);
            }
            catch { return 0; }
        }

        // ─── Layout ──────────────────────────────────────────────

        private void BuildTabRow(VisualElement root)
        {
            _tabRow = new VisualElement();
            _tabRow.AddToClassList("la-tab-row");
            root.Add(_tabRow);

            AddTab("inspector", "Inspector");
            AddTab("storage",   "Storage");
            AddTab("snapshots", "Snapshots");
            AddTab("activity",  "Activity");

            var spacer = new VisualElement();
            spacer.AddToClassList("la-tab-row__spacer");
            _tabRow.Add(spacer);

            // Sync widget — colored dot + label, plus pause/resume and stop/start toggles.
            BuildSyncWidget(_tabRow);

            var export = new Button(ExportClicked) { text = "Export" };
            export.AddToClassList("la-toolbar__btn");
            export.tooltip = "Export every loaded state to a single JSON file. Encrypted fields are exported as plaintext.";
            _tabRow.Add(export);

            var import = new Button(ImportClicked) { text = "Import" };
            import.AddToClassList("la-toolbar__btn");
            import.tooltip = "Import states from a JSON file. Live state is overwritten and saved; encrypted fields are re-encrypted by the kit on save.";
            _tabRow.Add(import);

            var reset = new Button(ResetAllClicked) { text = "Reset All" };
            reset.AddToClassList("la-toolbar__btn");
            reset.tooltip = "Wipe every cached state's persisted fields to their default values and save through every wired target. Destructive — confirm before applying.";
            _tabRow.Add(reset);

            var refresh = new Button(RefreshAll) { text = "Refresh" };
            refresh.AddToClassList("la-toolbar__btn");
            _tabRow.Add(refresh);

            var clearLog = new Button(() => { _log.Clear(); RefreshAll(); }) { text = "Clear Log" };
            clearLog.AddToClassList("la-toolbar__btn");
            _tabRow.Add(clearLog);
        }

        private void ExportClicked()
        {
            var defaultName = $"persistencekit_states_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var path = EditorUtility.SaveFilePanel("Export PersistenceKit States", "", defaultName, "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                int count = StateExportImport.ExportAll(path);
                int bytes = (int)Math.Min(int.MaxValue, new System.IO.FileInfo(path).Length);
                _log.Push(ActivityLog.Entry.ForExport(
                    DateTime.UtcNow,
                    System.IO.Path.GetFileName(path),
                    bytes,
                    $"{count} state(s) → {System.IO.Path.GetFileName(path)}"));

                Debug.Log($"[PersistenceKit] Exported {count} state(s) to {path}");
                EditorUtility.RevealInFinder(path);
                if (_activeTab == "activity") _activity.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Export failed", ex.Message, "OK");
            }
        }

        private async void ResetAllClicked()
        {
            // Snapshot the targets now so DeleteAsync's cache-eviction during iteration
            // doesn't pull the rug out from under us. Same trick as the import path.
            var pairs = new List<(PersistenceManager manager, IPersistentState state)>();
            foreach (var m in PersistenceManager.ActiveManagers)
                foreach (var s in m.SnapshotCache())
                    pairs.Add((m, s));

            if (pairs.Count == 0)
            {
                EditorUtility.DisplayDialog("Reset All", "No loaded states to reset.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Reset all states",
                    $"Reset {pairs.Count} cached state(s) to default values and save through every wired target?\n\n" +
                    "Every persisted field is wiped (strings → empty / null, numbers → 0, collections → null). " +
                    "Encrypted fields are re-encrypted on save. References in your runtime code stay valid — " +
                    "this rewrites field values in place, it does NOT evict the cache.",
                    "Reset", "Cancel"))
                return;

            try
            {
                _resetAllInFlight = true;
                int resetCount = 0;
                int fieldCount = 0;
                try
                {
                    foreach (var (manager, state) in pairs)
                    {
                        var fields = StateInspector.Inspect(state);
                        foreach (var fv in fields)
                        {
                            object defaultValue = null;
                            if (fv.FieldType.IsValueType)
                            {
                                try { defaultValue = Activator.CreateInstance(fv.FieldType); }
                                catch { /* leave null — shouldn't happen for value types */ }
                            }
                            try { fv.Set(defaultValue); }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"[PersistenceKit] Reset: {state.GetType().Name}.{fv.PropertyName} failed: {ex.Message}");
                                continue;
                            }
                            fieldCount++;
                        }
                        state.MarkDirty();
                        await manager.SaveAsync(state);
                        resetCount++;
                    }
                }
                finally { _resetAllInFlight = false; }

                _log?.Push(ActivityLog.Entry.ForReset(
                    DateTime.UtcNow,
                    $"{resetCount} state(s), {fieldCount} field(s) → defaults"));

                Debug.Log($"[PersistenceKit] Reset complete — {resetCount} state(s), {fieldCount} field(s) wiped to defaults.");
                RefreshAll();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Reset failed", ex.Message, "OK");
            }
        }

        private async void ImportClicked()
        {
            var path = EditorUtility.OpenFilePanel("Import PersistenceKit States", "", "json");
            if (string.IsNullOrEmpty(path)) return;

            if (!EditorUtility.DisplayDialog(
                    "Import States",
                    $"Import from\n\n{System.IO.Path.GetFileName(path)}\n\n" +
                    "Live state will be overwritten and immediately saved through every wired target. " +
                    "Encrypted fields will be re-encrypted by the kit on save.",
                    "Import", "Cancel"))
                return;

            try
            {
                _importInFlight = true;
                int imported, skipped;
                try
                {
                    (imported, skipped) = await StateExportImport.ImportAllAsync(path);
                }
                finally { _importInFlight = false; }

                int bytes = (int)Math.Min(int.MaxValue, new System.IO.FileInfo(path).Length);
                var summary = skipped > 0
                    ? $"{imported} imported, {skipped} skipped ← {System.IO.Path.GetFileName(path)}"
                    : $"{imported} state(s) ← {System.IO.Path.GetFileName(path)}";
                _log.Push(ActivityLog.Entry.ForImport(
                    DateTime.UtcNow,
                    System.IO.Path.GetFileName(path),
                    bytes,
                    summary));

                Debug.Log($"[PersistenceKit] Import complete — {imported} state(s) imported, {skipped} skipped.");
                RefreshAll();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Import failed", ex.Message, "OK");
            }
        }

        private void BuildSyncWidget(VisualElement host)
        {
            var widget = new VisualElement();
            widget.AddToClassList("la-sync");
            widget.tooltip = "Auto-save status (gray = no loop, green = active, yellow = paused, red = stopped).";

            _syncDot = new VisualElement();
            _syncDot.AddToClassList("la-sync__dot");
            widget.Add(_syncDot);

            _syncLabel = new Label("DISABLED");
            _syncLabel.AddToClassList("la-sync__label");
            widget.Add(_syncLabel);

            host.Add(widget);

            _pauseResumeBtn = new Button(TogglePauseResume) { text = "Pause" };
            _pauseResumeBtn.AddToClassList("la-toolbar__btn");
            host.Add(_pauseResumeBtn);

            _stopStartBtn = new Button(ToggleStopStart) { text = "Stop" };
            _stopStartBtn.AddToClassList("la-toolbar__btn");
            host.Add(_stopStartBtn);

            UpdateSyncWidget();
        }

        private static void TogglePauseResume()
        {
            // If anyone is paused, the next action is "resume all"; otherwise "pause all".
            var status = AutoSaveLoop.AggregateStatus();
            if (status == AutoSaveLoop.SyncStatus.Paused) AutoSaveLoop.ResumeAll();
            else                                          AutoSaveLoop.PauseAll();
        }

        private static void ToggleStopStart()
        {
            // If anyone is stopped, the next action is "start all"; otherwise "stop all".
            var status = AutoSaveLoop.AggregateStatus();
            if (status == AutoSaveLoop.SyncStatus.Stopped) AutoSaveLoop.StartAll();
            else                                            AutoSaveLoop.StopAll();
        }

        private void UpdateSyncWidget()
        {
            var status = AutoSaveLoop.AggregateStatus();

            // Reset dot state classes, then apply the matching one.
            _syncDot.RemoveFromClassList("la-sync__dot--disabled");
            _syncDot.RemoveFromClassList("la-sync__dot--active");
            _syncDot.RemoveFromClassList("la-sync__dot--paused");
            _syncDot.RemoveFromClassList("la-sync__dot--stopped");

            switch (status)
            {
                case AutoSaveLoop.SyncStatus.Disabled:
                    _syncDot.AddToClassList("la-sync__dot--disabled");
                    _syncLabel.text  = "DISABLED";
                    _pauseResumeBtn.SetEnabled(false);
                    _stopStartBtn.SetEnabled(false);
                    _pauseResumeBtn.text = "Pause";
                    _stopStartBtn.text   = "Stop";
                    break;
                case AutoSaveLoop.SyncStatus.Active:
                    _syncDot.AddToClassList("la-sync__dot--active");
                    _syncLabel.text  = "ACTIVE";
                    _pauseResumeBtn.SetEnabled(true);
                    _stopStartBtn.SetEnabled(true);
                    _pauseResumeBtn.text = "Pause";
                    _stopStartBtn.text   = "Stop";
                    break;
                case AutoSaveLoop.SyncStatus.Paused:
                    _syncDot.AddToClassList("la-sync__dot--paused");
                    _syncLabel.text  = "PAUSED";
                    _pauseResumeBtn.SetEnabled(true);
                    _stopStartBtn.SetEnabled(true);
                    _pauseResumeBtn.text = "Resume";
                    _stopStartBtn.text   = "Stop";
                    break;
                case AutoSaveLoop.SyncStatus.Stopped:
                    _syncDot.AddToClassList("la-sync__dot--stopped");
                    _syncLabel.text  = "STOPPED";
                    _pauseResumeBtn.SetEnabled(false);
                    _stopStartBtn.SetEnabled(true);
                    _pauseResumeBtn.text = "Pause";
                    _stopStartBtn.text   = "Start";
                    break;
            }
        }

        private void AddTab(string id, string label)
        {
            var tab = new Label(label);
            tab.AddToClassList("la-tab");
            tab.userData = id;
            // MouseUp on the element body — drag-off cancels the click as expected.
            tab.RegisterCallback<MouseUpEvent>(_ => SelectTab(id));
            _tabRow.Add(tab);
        }

        private void BuildBody(VisualElement root)
        {
            _body = new VisualElement();
            _body.AddToClassList("la-body");
            root.Add(_body);

            _inspector = new InspectorTab(_log);
            _storage   = new StorageTab();
            _activity  = new ActivityTab(_log, RollbackEntry);
            _snapshots = new SnapshotsTab();

            _body.Add(_inspector.Root);
            _body.Add(_storage.Root);
            _body.Add(_activity.Root);
            _body.Add(_snapshots.Root);
        }

        private void BuildStatusBar(VisualElement root)
        {
            var bar = new VisualElement();
            bar.AddToClassList("la-statusbar");
            root.Add(bar);

            var left = new VisualElement();
            left.AddToClassList("la-statusbar__left");
            _statusLeft = new Label();
            left.Add(_statusLeft);
            bar.Add(left);

            var right = new VisualElement();
            right.AddToClassList("la-statusbar__right");
            _statusRight = new Label("ready");
            _statusRight.AddToClassList("la-statusbar__hint");
            right.Add(_statusRight);
            bar.Add(right);
        }

        // ─── Tabs ────────────────────────────────────────────────

        private void SelectTab(string id)
        {
            _activeTab = id;
            foreach (var ve in _tabRow.Children())
            {
                if (ve is Label l && l.userData is string lid)
                {
                    l.EnableInClassList("la-tab--active", lid == id);
                }
            }
            _inspector.Root.style.display = id == "inspector" ? DisplayStyle.Flex : DisplayStyle.None;
            _storage.Root.style.display   = id == "storage"   ? DisplayStyle.Flex : DisplayStyle.None;
            _activity.Root.style.display  = id == "activity"  ? DisplayStyle.Flex : DisplayStyle.None;
            _snapshots.Root.style.display = id == "snapshots" ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshAll();
        }

        // ─── Live refresh ────────────────────────────────────────

        private void LiveRefresh()
        {
            // Cheap repaint loop: subscribe to any new managers; redraw the active tab.
            SubscribeNewManagers();
            CaptureInitialSnapshots();
            switch (_activeTab)
            {
                case "inspector": _inspector.Refresh(); break;
                case "storage":   _storage.Refresh(); break;
                case "activity":  _activity.Refresh(); break;
                case "snapshots": _snapshots.Refresh(); break;
            }
            UpdateSyncWidget();
            UpdateStatus();
        }

        /// <summary>
        /// Capture pre-mutation field snapshots for states we haven't seen yet, so the very
        /// first save event has a meaningful "before" to diff against.
        /// </summary>
        private void CaptureInitialSnapshots()
        {
            foreach (var m in PersistenceManager.ActiveManagers)
            {
                var states = m.SnapshotCache();
                for (int i = 0; i < states.Count; i++)
                    if (!_snapshottedStates.Contains(states[i]))
                        DiffAndUpdateSnapshot(states[i]);   // first-pass: captures, doesn't log
            }
        }

        private void RefreshAll()
        {
            SubscribeNewManagers();
            _inspector.Refresh();
            _storage.Refresh();
            _activity.Refresh();
            _snapshots.Refresh();
            UpdateSyncWidget();
            UpdateStatus();
            _lastRefreshTime = EditorApplication.timeSinceStartup;
        }

        private void SubscribeNewManagers()
        {
            var current = PersistenceManager.ActiveManagers;
            // Add subscriptions for managers we haven't seen yet.
            foreach (var m in current)
            {
                if (_subscribed.Add(m))
                    m.OnSaved += OnManagerSaved;
            }
            // Drop subscriptions for managers no longer alive.
            _subscribed.RemoveWhere(m => !current.Contains(m));
        }

        private void UnsubscribeAll()
        {
            foreach (var m in _subscribed) m.OnSaved -= OnManagerSaved;
            _subscribed.Clear();
        }

        private void OnManagerSaved(PersistenceManager.SaveEvent e)
        {
            // Resolve the saved state by key so we can diff its current field values against
            // our snapshot. Same-key matches across managers should be vanishingly rare in
            // practice, but if it happens we just log against the first hit.
            IPersistentState saved = null;
            foreach (var m in PersistenceManager.ActiveManagers)
            {
                foreach (var s in m.SnapshotCache())
                    if (s.Key == e.Key) { saved = s; break; }
                if (saved != null) break;
            }

            // Always update snapshots so the next non-import save has a fresh baseline.
            List<ActivityLog.FieldChange> changes = null;
            if (saved != null)
                changes = DiffAndUpdateSnapshot(saved);

            // Drop the per-state save entry while an import, snapshot restore, or reset
            // is in flight — the single summary entry already covers what happened.
            if (_importInFlight || _snapshotRestoreInFlight || _resetAllInFlight) return;

            _log.Push(new ActivityLog.Entry(e.At, e.Key, e.Target, e.Bytes, changes));
        }

        private List<ActivityLog.FieldChange> DiffAndUpdateSnapshot(IPersistentState state)
        {
            // First sighting — capture and skip the diff. Without a baseline every field
            // would otherwise show up as "changed" with no meaningful "before" value.
            bool firstSnapshot = _snapshottedStates.Add(state);

            var fields   = StateInspector.Inspect(state);
            var typeName = state.GetType().Name;
            List<ActivityLog.FieldChange> changes = null;

            for (int i = 0; i < fields.Count; i++)
            {
                var fv = fields[i];
                object current;
                try { current = fv.Get(); }
                catch { continue; }

                var key  = (state, fv.PropertyName);
                var snap = ToSnapshot(current);

                if (!firstSnapshot && _fieldSnapshots.TryGetValue(key, out var prevSnap) && prevSnap != snap)
                {
                    changes ??= new List<ActivityLog.FieldChange>();
                    changes.Add(new ActivityLog.FieldChange(typeName, fv.FieldName, fv.PropertyName, prevSnap, snap));
                }

                _fieldSnapshots[key] = snap;
            }

            return changes;
        }

        /// <summary>JSON-encode every value (including primitives) so comparison and roundtrip stay consistent.</summary>
        internal static string ToSnapshot(object v)
        {
            if (v == null) return "null";
            try { return Newtonsoft.Json.JsonConvert.SerializeObject(v); }
            catch { return v.ToString() ?? "null"; }
        }

        /// <summary>Format a JSON snapshot for inline display — strips outer quotes from strings, truncates long values.</summary>
        internal static string FormatSnapshot(string snapshot)
        {
            if (string.IsNullOrEmpty(snapshot)) return "null";
            const int max = 80;
            return snapshot.Length <= max ? snapshot : snapshot.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// Rolls back every change in <paramref name="entry"/> by deserialising the captured
        /// "old" snapshot back into each field, marking the corresponding target dirty, and
        /// kicking the autosave loop. The rollback itself produces a fresh activity entry.
        /// </summary>
        internal void RollbackEntry(ActivityLog.Entry entry)
        {
            if (entry.Changes == null || entry.Changes.Count == 0) return;

            // Find the live state instance by key.
            IPersistentState target = null;
            foreach (var m in PersistenceManager.ActiveManagers)
            {
                foreach (var s in m.SnapshotCache())
                    if (s.Key == entry.Key) { target = s; break; }
                if (target != null) break;
            }
            if (target == null)
            {
                Debug.LogWarning($"[PersistenceKit] Rollback skipped — state '{entry.Key}' is no longer cached.");
                return;
            }

            var t = target.GetType();
            var bf = System.Reflection.BindingFlags.Instance |
                     System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.NonPublic |
                     System.Reflection.BindingFlags.DeclaredOnly;

            int applied = 0;
            for (int i = 0; i < entry.Changes.Count; i++)
            {
                var c = entry.Changes[i];
                var field = t.GetField(c.FieldName, bf);
                if (field == null)
                {
                    Debug.LogWarning($"[PersistenceKit] Rollback: field '{c.FieldName}' not found on {t.Name}.");
                    continue;
                }
                try
                {
                    var oldValue = Newtonsoft.Json.JsonConvert.DeserializeObject(c.OldSnapshot, field.FieldType);
                    field.SetValue(target, oldValue);
                    applied++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[PersistenceKit] Rollback: failed to restore {t.Name}.{c.PropertyName}: {ex.Message}");
                }
            }

            if (applied == 0) return;

            // Leave _fieldSnapshots untouched: the snapshot still holds the post-mutation
            // value, so when the imminent autosave fires DiffAndUpdateSnapshot will see
            // "snapshot=new vs current=old" and log the rollback as its own change line.
            // Mark only the target this save event belonged to.
            target.MarkDirty(entry.Target);

            Debug.Log($"[PersistenceKit] Rolled back {applied} field(s) on {t.Name}; autosave will flush.");
        }

        private void UpdateStatus()
        {
            var managers = PersistenceManager.ActiveManagers;
            int totalCached = 0;
            long totalSaves = 0;
            foreach (var m in managers)
            {
                totalCached += m.SnapshotCache().Count;
                totalSaves  += m.SaveCount;
            }
            _statusLeft.text = $"managers: {managers.Count}    cached: {totalCached}    saves: {totalSaves}    log: {_log.Count}";
            _statusRight.text = managers.Count == 0
                ? "no active manager — enter Play mode or build one in code"
                : $"updated {(int)(EditorApplication.timeSinceStartup - _lastRefreshTime + 0.5)}s ago";
        }

        private static StyleSheet LoadStyleSheet()
        {
            // Find the .uss alongside this script regardless of where the kit was placed.
            var guids = AssetDatabase.FindAssets("PersistenceKitWindow t:StyleSheet");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileName(path).Equals(UssGuidProbeName, StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            }
            // Fallback — first match.
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }

        // ─── Shared helpers exposed to tabs ──────────────────────

        internal static VisualElement Heading(string text)
        {
            var h = new Label(text.ToUpperInvariant());
            h.AddToClassList("la-heading");
            return h;
        }

        internal static VisualElement Card(string title, params VisualElement[] children)
        {
            var card = new VisualElement();
            card.AddToClassList("la-card");
            if (!string.IsNullOrEmpty(title))
            {
                var t = new Label(title.ToUpperInvariant());
                t.AddToClassList("la-card__title");
                card.Add(t);
            }
            foreach (var c in children) card.Add(c);
            return card;
        }

        internal static VisualElement Hint(string text)
        {
            var h = new Label(text);
            h.AddToClassList("la-hint");
            return h;
        }

        internal static Label Chip(string text, string variantClass)
        {
            var l = new Label(text.ToUpperInvariant());
            l.AddToClassList("la-chip");
            if (!string.IsNullOrEmpty(variantClass)) l.AddToClassList(variantClass);
            return l;
        }

        internal static string TargetChipClass(PersistTarget t) => t switch
        {
            PersistTarget.Json        => "la-chip--json",
            PersistTarget.Binary      => "la-chip--binary",
            PersistTarget.PlayerPrefs => "la-chip--playerprefs",
            PersistTarget.Remote      => "la-chip--remote",
            _ => "",
        };

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
        }

        // ─── Robust icons ────────────────────────────────────────
        // Unity's default UI Toolkit font ships a Latin/symbol subset only — emoji (🔒) and
        // many arrow glyphs (↗ ↶) render as empty "tofu" boxes. Prefer the editor's built-in
        // vector icons; fall back to a plain-ASCII label so the control is always legible.

        /// <summary>A small padlock indicator for encrypted fields — built-in icon, ASCII fallback.</summary>
        internal static VisualElement LockIndicator(string tooltip)
        {
            var tex = LoadBuiltinIcon("LockIcon", "InspectorLock", "AssemblyLock");
            if (tex != null)
            {
                var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
                img.AddToClassList("la-field__lock");
                img.tooltip = tooltip;
                return img;
            }
            var lbl = new Label("ENC");
            lbl.AddToClassList("la-chip");
            lbl.AddToClassList("la-chip--encrypted");
            lbl.tooltip = tooltip;
            return lbl;
        }

        /// <summary>
        /// A compact square button that shows a built-in editor icon when available and a
        /// short ASCII label otherwise (never an unrenderable glyph).
        /// </summary>
        internal static Button IconButton(Action onClick, string fallbackText, string tooltip, params string[] builtinIconNames)
        {
            var btn = new Button(onClick);
            btn.AddToClassList("la-toolbar__btn");
            btn.AddToClassList("la-toolbar__btn--icon");
            btn.tooltip = tooltip;

            var tex = LoadBuiltinIcon(builtinIconNames);
            if (tex != null)
            {
                var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
                img.AddToClassList("la-icon-btn__img");
                img.pickingMode = PickingMode.Ignore;
                btn.Add(img);
            }
            else
            {
                btn.text = fallbackText;
            }
            return btn;
        }

        private static Texture LoadBuiltinIcon(params string[] names)
        {
            // Probe with FindTexture, NOT IconContent: IconContent logs a console error for
            // every unknown icon name (e.g. "Unable to load the icon: 'd_LockIcon'"), which
            // spams the console since we try several optional candidates. FindTexture returns
            // null silently, so probing is safe.
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                try
                {
                    // Dark-skin variant first when in pro skin, then the base name.
                    if (EditorGUIUtility.isProSkin)
                    {
                        var dark = EditorGUIUtility.FindTexture("d_" + n);
                        if (dark != null) return dark;
                    }
                    var tex = EditorGUIUtility.FindTexture(n);
                    if (tex != null) return tex;
                }
                catch { /* unknown icon name — try the next candidate */ }
            }
            return null;
        }
    }
}
