using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PersistenceKit.Editor.Settings;
using PersistenceKit.Internals;
using PersistenceKit.Targets;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector.Editor;
#endif

namespace PersistenceKit.Editor.Tabs
{
    /// <summary>
    /// Left sidebar: filter + Manager → Type → Slot tree. Center: an edit-mode banner, a
    /// summary strip, and the editable field grid for the selected state. Right sidebar:
    /// action card (Save / Reload / Delete) + an info card summarising target mask and
    /// dirty bits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tab draws whatever is in <see cref="PersistenceManager.ActiveManagers"/> and does
    /// not care who built them. In Play mode that's the game's manager; outside it, it's the
    /// one <see cref="EditModeSession"/> builds against the save files, which is what makes
    /// states viewable and resettable without pressing Play.
    /// </para>
    /// <para>
    /// Refresh strategy is "rebuild only when structure changes":
    /// </para>
    ///   - Tree: full rebuild only when the manager set, the per-manager state-key set, the
    ///     filter or the load-error set changes; every other tick walks the cached row entries
    ///     and toggles the dirty dots + selection class in place. This kills the hover-flicker
    ///     that comes from blowing away rows during a hover.
    ///   - Field grid: full rebuild only when <c>_selState</c> changes; otherwise each editor
    ///     pulls the live value via a per-field refresh delegate, skipping any editor that
    ///     currently has focus so the user's typing isn't clobbered.
    ///   - Anything that costs real work — the overview's disk scan, the changed-field diff —
    ///     runs on its own throttle rather than at the refresh tick.
    /// </remarks>
    internal sealed class InspectorTab
    {
        private readonly ActivityLog _log;

        public VisualElement Root { get; }

        private readonly VisualElement _treeList;
        private readonly VisualElement _fieldList;
        private readonly VisualElement _actionsHost;
        private readonly Label         _selectionTitle;

        // Edit-mode banner (canvas header) — only visible outside Play mode.
        private readonly VisualElement _editModeBar;
        private readonly Label         _editModeStatus;
        private readonly Button        _editModeLoadBtn;

        // Overview strip.
        private readonly VisualElement _overview;

        // Tree filter.
        private readonly TextField _search;
        private string _searchText = string.Empty;

        // Selection.
        private PersistenceManager _selManager;
        private IPersistentState   _selState;
        private IPersistentState   _builtForState;
        private IPersistentState   _actionsBuiltForState;

        // Live-updated labels in the Info card — refreshed in place each tick so the whole
        // right sidebar doesn't get torn down and rebuilt at the refresh interval.
        private Label _infoDirtyValue;

        // Tree caching.
        private string _treeStructureSig;
        private readonly List<TreeRowEntry> _treeRows = new List<TreeRowEntry>();

        // Field-grid caching.
        private readonly List<FieldRefreshEntry> _fieldRefs = new List<FieldRefreshEntry>();

        // Per-type snapshot of a freshly-constructed instance's field values, used to tint
        // fields that differ from their default. Built once per type on first sighting —
        // constructing a throwaway state every refresh tick would be wasteful.
        private readonly Dictionary<Type, Dictionary<string, string>> _defaultSnapshots
            = new Dictionary<Type, Dictionary<string, string>>();

        // Overview is the only part of this tab that touches the disk, so it recomputes on a
        // slower cadence than the ~3 Hz refresh loop rather than stat'ing files every tick.
        private double _overviewNextRefresh;
        private string _overviewSig;
        private const double OverviewIntervalSeconds = 2.0;

        private double _changedNextRefresh;
        private const double ChangedIntervalSeconds = 0.5;

        // Transient ScriptableObjects used to host complex field values for the IMGUI
        // inspector. Tracked here so we can destroy them on rebuild / window close.
        private readonly List<UnityEngine.Object> _wrapperSOs = new List<UnityEngine.Object>();

        // Per-field Odin trees / other IDisposables created during BuildFieldRow. Disposed
        // alongside the wrapper SOs.
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private sealed class TreeRowEntry
        {
            public PersistenceManager Manager;
            public IPersistentState   State;
            public VisualElement      Row;

            /// <summary>One dot per target in the state's mask, re-tinted each tick from the dirty bits.</summary>
            public readonly List<(PersistTarget target, VisualElement dot)> Dots
                = new List<(PersistTarget, VisualElement)>();
        }

        private sealed class FieldRefreshEntry
        {
            public VisualElement Editor;
            public VisualElement Row;
            public Action        PullFromState;   // copies state→editor without firing change callbacks
            public Func<bool>    DiffersFromDefault;
        }

        /// <summary>
        /// Transient SO used to host a single <c>[Serializable]</c> object so Unity's stock
        /// inspector (via <c>EditorGUILayout.PropertyField</c>) can render it. One instance
        /// per object-typed field; tracked in <c>_wrapperSOs</c> and destroyed on rebuild.
        /// </summary>
        private sealed class FieldWrapperSO : ScriptableObject
        {
            [SerializeReference] public object value;
        }


        public InspectorTab(ActivityLog log)
        {
            _log = log;
            Root = new VisualElement();
            Root.AddToClassList("la-body");
            Root.style.flexGrow = 1;

            // Left sidebar: filter + tree
            var left = new VisualElement();
            left.AddToClassList("la-sidebar");
            left.Add(PersistenceKitWindow.Heading("Loaded States"));

            _search = new TextField();
            _search.AddToClassList("la-search");
            _search.textEdition.placeholder = "Filter type or slot…";
            _search.textEdition.hidePlaceholderOnFocus = true;
            _search.tooltip = "Substring match against the state's type name, slot and storage key.";
            _search.RegisterValueChangedCallback(e =>
            {
                _searchText = e.newValue ?? string.Empty;
                _treeStructureSig = null;      // filter is part of the tree's shape
                RefreshTree();
            });
            var searchHost = new VisualElement();
            searchHost.AddToClassList("la-search-host");
            searchHost.Add(_search);
            left.Add(searchHost);

            var leftScroll = new ScrollView(ScrollViewMode.Vertical);
            _treeList = new VisualElement();
            leftScroll.Add(_treeList);
            left.Add(leftScroll);
            Root.Add(left);

            // Canvas: edit-mode banner + overview + selected state's fields
            var canvas = new VisualElement();
            canvas.AddToClassList("la-canvas");

            _editModeBar = new VisualElement();
            _editModeBar.AddToClassList("la-editmode");
            _editModeStatus = new Label();
            _editModeStatus.AddToClassList("la-editmode__status");
            _editModeBar.Add(_editModeStatus);
            _editModeLoadBtn = new Button(LoadEditModeStates) { text = "Load Saved States" };
            _editModeLoadBtn.AddToClassList("la-toolbar__btn");
            _editModeLoadBtn.tooltip = "Build an editor-only manager and read every saved state from storage.";
            _editModeBar.Add(_editModeLoadBtn);
            var slotBtn = new Button(LoadSlotPrompt) { text = "Load Slot…" };
            slotBtn.AddToClassList("la-toolbar__btn");
            slotBtn.tooltip = "Open a named slot by hand. Needed for slots that live only in PlayerPrefs, which can't be enumerated.";
            _editModeBar.Add(slotBtn);
            _editModeBar.style.display = DisplayStyle.None;
            canvas.Add(_editModeBar);

            _overview = new VisualElement();
            _overview.AddToClassList("la-overview");
            canvas.Add(_overview);

            _selectionTitle = new Label("Select a state on the left");
            _selectionTitle.AddToClassList("la-heading");
            canvas.Add(_selectionTitle);

            var fieldsScroll = new ScrollView(ScrollViewMode.Vertical);
            _fieldList = new VisualElement();
            fieldsScroll.Add(_fieldList);
            canvas.Add(fieldsScroll);
            Root.Add(canvas);

            // Right sidebar: actions
            var right = new VisualElement();
            right.AddToClassList("la-sidebar");
            right.AddToClassList("la-sidebar--right");
            right.Add(PersistenceKitWindow.Heading("Actions"));
            _actionsHost = new VisualElement();
            right.Add(_actionsHost);
            Root.Add(right);
        }

        /// <summary>Destroy any transient ScriptableObjects this tab owns.</summary>
        public void Dispose() => DisposeWrappers();

        public void Refresh()
        {
            UpdateEditModeBar();
            RefreshTree();
            UpdateOverview();

            if (!ReferenceEquals(_selState, _builtForState))
            {
                RebuildFields();
                _builtForState = _selState;
            }
            else
            {
                // Same selection — push live values into editors that don't have focus.
                RefreshFieldValues();
            }

            RebuildActions();
        }

        // ─── Edit-mode banner ────────────────────────────────────

        /// <summary>
        /// Outside Play mode there is no game manager, so the tab offers to build its own and
        /// read the save files directly. In Play mode the banner disappears entirely — the
        /// game's manager is the only source of truth then.
        /// </summary>
        private void UpdateEditModeBar()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _editModeBar.style.display = DisplayStyle.None;
                return;
            }
            _editModeBar.style.display = DisplayStyle.Flex;

            if (!EditModeSession.SerializerAvailable)
            {
                _editModeStatus.text = "Newtonsoft JSON is not installed — saved states can't be read outside Play mode.";
                _editModeBar.EnableInClassList("la-editmode--warn", true);
                _editModeLoadBtn.SetEnabled(false);
                return;
            }

            _editModeLoadBtn.SetEnabled(true);
            _editModeLoadBtn.text = EditModeSession.IsRunning ? "Reload From Disk" : "Load Saved States";

            var error = EditModeSession.LastError;
            if (!string.IsNullOrEmpty(error))
            {
                _editModeStatus.text = "Edit mode — " + error;
                _editModeBar.EnableInClassList("la-editmode--warn", true);
                return;
            }

            int failed = EditModeSession.LoadErrors.Count;

            // No key + [Encrypted] fields somewhere means those states can't be opened or
            // saved. Say so up front rather than letting the user find out by clicking Save
            // and reading an exception.
            bool missingKey = EditModeSession.IsRunning
                              && !EditModeSession.HasEncryptor
                              && EditModeSession.AnyEncryptedFields();

            _editModeBar.EnableInClassList("la-editmode--warn", failed > 0 || missingKey);

            if (!EditModeSession.IsRunning)
            {
                int types = EditModeSession.RegisteredTypeCount;
                _editModeStatus.text = types == 0
                    ? "Edit mode — no [PersistentState] types found in this project."
                    : $"Edit mode — {types} registered state type(s). Load them to view and edit saved data.";
            }
            else if (missingKey)
            {
                _editModeStatus.text = "Edit mode — no encryption key set, so states with [Encrypted] fields " +
                                       "can't be opened or saved. Set it in Project Settings → PersistenceKit → Edit Mode.";
            }
            else if (failed > 0)
            {
                _editModeStatus.text = $"Edit mode — editing saved data directly. {failed} state(s) failed to open (see the tree).";
            }
            else
            {
                _editModeStatus.text = "Edit mode — editing saved data directly. Changes save straight to storage.";
            }
        }

        private void LoadEditModeStates()
        {
            Run(async () =>
            {
                await EditModeSession.ReloadAsync();
                _treeStructureSig = null;
                _overviewSig = null;
                // The manager was rebuilt, so the old selection points at a dead instance.
                _selState = null;
                _selManager = null;
                Refresh();
            });
        }

        /// <summary>
        /// Open a slot by name. PlayerPrefs and Remote have no enumeration API, so a slot that
        /// lives only there is invisible to the disk scan and has to be asked for explicitly.
        /// </summary>
        private void LoadSlotPrompt()
        {
            var registered = PersistentStateRegistry.Snapshot();
            if (registered.Count == 0)
            {
                EditorUtility.DisplayDialog("Load Slot", "No [PersistentState] types are registered in this project.", "OK");
                return;
            }

            var menu = new GenericMenu();
            foreach (var rs in registered.OrderBy(r => r.Type.Name))
            {
                var captured = rs;
                menu.AddItem(new GUIContent(captured.Type.Name), false, () =>
                {
                    var slot = SlotPromptWindow.Prompt(captured.Type.Name);
                    if (slot == null) return;      // cancelled
                    Run(async () =>
                    {
                        await EditModeSession.LoadSlotAsync(captured.Type, slot);
                        _treeStructureSig = null;
                        _overviewSig = null;
                        Refresh();
                    });
                });
            }
            menu.ShowAsContext();
        }

        // ─── Tree ────────────────────────────────────────────────

        private void RefreshTree()
        {
            var managers = PersistenceManager.ActiveManagers;

            // A manager can vanish under us — Play mode starting tears the edit-mode session
            // down, and reloading rebuilds it. Drop the dangling selection before the actions
            // sidebar tries to save through a disposed manager.
            if (_selManager != null && !managers.Contains(_selManager))
            {
                _selManager = null;
                _selState   = null;
            }

            var sig = ComputeStructureSignature(managers, _searchText);

            if (sig == _treeStructureSig)
            {
                UpdateTreeRowStates();    // fast path — toggle pills + selection class only
                return;
            }
            _treeStructureSig = sig;

            // Structure changed — rebuild.
            _treeList.Clear();
            _treeRows.Clear();

            if (managers.Count == 0)
            {
                _treeList.Add(PersistenceKitWindow.Hint(EditorApplication.isPlayingOrWillChangePlaymode
                    ? "No active PersistenceManager. Call PersistenceKitBuilder.Build() somewhere to populate this view."
                    : "No states loaded. Press 'Load Saved States' above to read them straight from storage, or enter Play mode to inspect the running game."));
                return;
            }

            int matched = 0;
            for (int mi = 0; mi < managers.Count; mi++)
            {
                var manager = managers[mi];
                var managerLabel = new Label(DescribeManager(manager, mi));
                managerLabel.AddToClassList("la-heading");
                managerLabel.AddToClassList("la-heading--sub");
                _treeList.Add(managerLabel);

                var states = manager.SnapshotCache();
                var byType = new Dictionary<Type, List<IPersistentState>>();
                foreach (var s in states)
                {
                    if (!Matches(s)) continue;
                    if (!byType.TryGetValue(s.GetType(), out var list))
                        byType[s.GetType()] = list = new List<IPersistentState>();
                    list.Add(s);
                }

                if (byType.Count == 0)
                {
                    var empty = new Label(states.Count == 0 ? "(no states loaded)" : "(no matches)");
                    empty.AddToClassList("la-note");
                    empty.AddToClassList("la-note--empty");
                    _treeList.Add(empty);
                    continue;
                }

                foreach (var kv in byType.OrderBy(p => p.Key.Name))
                {
                    var typeRow = new VisualElement();
                    typeRow.AddToClassList("la-row");
                    typeRow.AddToClassList("la-row--type");
                    var typeLabel = new Label(kv.Key.Name);
                    typeLabel.AddToClassList("la-row__label");
                    typeRow.Add(typeLabel);
                    var count = new Label(kv.Value.Count.ToString());
                    count.AddToClassList("la-row__count");
                    typeRow.Add(count);
                    _treeList.Add(typeRow);

                    foreach (var s in kv.Value.OrderBy(SlotOf, StringComparer.Ordinal))
                    {
                        matched++;
                        _treeList.Add(BuildStateRow(manager, s));
                    }
                }
            }

            // Everything was filtered out — say so once rather than leaving an empty panel.
            if (matched == 0 && _searchText.Length > 0)
                _treeList.Add(PersistenceKitWindow.Hint($"Nothing matches \"{_searchText}\"."));

            AppendLoadErrors();
            UpdateTreeRowStates();
        }

        private VisualElement BuildStateRow(PersistenceManager manager, IPersistentState s)
        {
            var row = new VisualElement();
            row.AddToClassList("la-row");
            row.AddToClassList("la-row--state");

            var slotName = SlotOf(s);
            var label = new Label(slotName.Length == 0 ? "<default>" : slotName);
            label.AddToClassList("la-row__label");
            label.tooltip = s.Key;
            row.Add(label);

            // One dot per target the state writes to: lit when that target has unsaved
            // writes, dim when it's clean. Replaces the single all-or-nothing DIRTY pill —
            // "which store is behind?" is the question you actually have mid-session.
            var dots = new VisualElement();
            dots.AddToClassList("la-row__dots");
            var entry = new TreeRowEntry { Manager = manager, State = s, Row = row };
            var mask = (byte)s.TargetMask;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var target = (PersistTarget)i;
                var dot = new VisualElement();
                dot.AddToClassList("la-dot");
                dot.AddToClassList(DotClass(target));
                dot.tooltip = target.ToString();
                dots.Add(dot);
                entry.Dots.Add((target, dot));
            }
            row.Add(dots);

            var capturedManager = manager;
            var capturedState   = s;
            row.RegisterCallback<MouseUpEvent>(_ =>
            {
                _selManager = capturedManager;
                _selState   = capturedState;
                // Only flip selection class instead of a full refresh — avoids
                // collapsing the hover state under the user's cursor.
                UpdateTreeRowStates();
                // Field grid + actions: trigger via the normal refresh path.
                Refresh();
            });
            _treeRows.Add(entry);
            return row;
        }

        /// <summary>Render the states that threw while loading, so a failure isn't just an absence.</summary>
        private void AppendLoadErrors()
        {
            var errors = EditModeSession.LoadErrors;
            if (errors.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var heading = new Label("FAILED TO OPEN");
            heading.AddToClassList("la-heading");
            heading.AddToClassList("la-heading--sub");
            _treeList.Add(heading);

            foreach (var kv in errors.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var row = new VisualElement();
                row.AddToClassList("la-row");
                row.AddToClassList("la-row--error");
                var label = new Label(kv.Key);
                label.AddToClassList("la-row__label");
                label.tooltip = kv.Value;
                row.Add(label);
                _treeList.Add(row);
            }
        }

        private static string DotClass(PersistTarget t) => t switch
        {
            PersistTarget.Json        => "la-dot--json",
            PersistTarget.Binary      => "la-dot--binary",
            PersistTarget.PlayerPrefs => "la-dot--playerprefs",
            PersistTarget.Remote      => "la-dot--remote",
            _ => "",
        };

        private static string DescribeManager(PersistenceManager m, int index)
            => EditModeSession.Owns(m) ? "Edit Mode — Saved Data" : $"Manager #{index + 1}";

        private bool Matches(IPersistentState s)
        {
            if (_searchText.Length == 0) return true;
            return s.GetType().Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0
                || s.Key.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateTreeRowStates()
        {
            bool showDirty = PersistenceKitSettings.Instance.ShowDirtyChips;
            foreach (var e in _treeRows)
            {
                e.Row.EnableInClassList("la-row--selected", ReferenceEquals(e.State, _selState));
                var mask = (byte)e.Manager.Dirty.Peek(e.State.Key);
                for (int i = 0; i < e.Dots.Count; i++)
                {
                    var (target, dot) = e.Dots[i];
                    bool dirty = showDirty && (mask & (1 << (int)target)) != 0;
                    dot.EnableInClassList("la-dot--dirty", dirty);
                }
            }
        }

        private string ComputeStructureSignature(List<PersistenceManager> managers, string filter)
        {
            // Hash of (manager identity, ordered set of matching state keys per manager) plus
            // the filter and the load-error set. Rebuild is triggered whenever this changes;
            // dirty-bit changes do NOT bump the sig.
            var sb = new StringBuilder(64);
            sb.Append(filter).Append('#');
            for (int i = 0; i < managers.Count; i++)
            {
                sb.Append(managers[i].GetHashCode()).Append('@');
                var states = managers[i].SnapshotCache();
                for (int j = 0; j < states.Count; j++)
                    if (Matches(states[j])) sb.Append(states[j].Key).Append(',');
                sb.Append(';');
            }
            sb.Append('!');
            foreach (var k in EditModeSession.LoadErrors.Keys) sb.Append(k).Append(',');
            return sb.ToString();
        }

        // ─── Field grid ──────────────────────────────────────────

        private void RebuildFields()
        {
            DisposeWrappers();
            _fieldList.Clear();
            _fieldRefs.Clear();

            if (_selState == null)
            {
                _selectionTitle.text = "SELECT A STATE ON THE LEFT";
                return;
            }

            var t = _selState.GetType();
            _selectionTitle.text = $"{t.Name}    KEY={_selState.Key}";

            var fields = StateInspector.Inspect(_selState);
            if (fields.Count == 0)
            {
                _fieldList.Add(PersistenceKitWindow.Hint("This state has no [Persist] fields."));
                return;
            }

            if (PersistenceKitSettings.Instance.GroupFieldsByTarget)
            {
                // Group under the store each field actually lands in. A state's fields can fan
                // out across several targets, and "what will this save write, and where" is
                // hard to read off a flat list of colour chips.
                foreach (var group in fields.GroupBy(f => f.Target).OrderBy(g => g.Key))
                {
                    _fieldList.Add(FieldGroupHeader(group.Key, group.Count()));
                    foreach (var fv in group)
                        _fieldList.Add(BuildFieldRow(fv));
                }
            }
            else
            {
                foreach (var fv in fields)
                    _fieldList.Add(BuildFieldRow(fv));
            }

            UpdateChangedHighlights(force: true);
        }

        private VisualElement FieldGroupHeader(PersistTarget target, int count)
        {
            var header = new VisualElement();
            header.AddToClassList("la-fieldgroup");
            header.Add(PersistenceKitWindow.Chip(target.ToString(), PersistenceKitWindow.TargetChipClass(target)));
            var label = new Label($"{count} field{(count == 1 ? "" : "s")}");
            label.AddToClassList("la-fieldgroup__count");
            header.Add(label);
            return header;
        }

        private void RefreshFieldValues()
        {
            // Pull live values from the state into each editor that isn't currently focused.
            for (int i = 0; i < _fieldRefs.Count; i++)
            {
                var fr = _fieldRefs[i];
                if (HasFocusWithin(fr.Editor)) continue;
                try { fr.PullFromState(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            UpdateChangedHighlights(force: false);
        }

        /// <summary>
        /// Tint every field whose value has moved off the type's constructed default.
        /// </summary>
        /// <remarks>
        /// Each check JSON-encodes the field's current value, so this runs on a ~500 ms
        /// throttle rather than at the tab's refresh rate — a state holding a large collection
        /// would otherwise re-serialise it several times a second just to colour a border.
        /// </remarks>
        private void UpdateChangedHighlights(bool force)
        {
            if (!PersistenceKitSettings.Instance.HighlightChangedFields)
            {
                if (force)
                    foreach (var fr in _fieldRefs)
                        fr.Row?.EnableInClassList("la-field--changed", false);
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!force && now < _changedNextRefresh) return;
            _changedNextRefresh = now + ChangedIntervalSeconds;

            for (int i = 0; i < _fieldRefs.Count; i++)
            {
                var fr = _fieldRefs[i];
                if (fr.Row == null || fr.DiffersFromDefault == null) continue;
                bool changed;
                try { changed = fr.DiffersFromDefault(); }
                catch { continue; }
                fr.Row.EnableInClassList("la-field--changed", changed);
            }
        }

        /// <summary>
        /// Field-name → JSON snapshot of a freshly-constructed instance of <paramref name="t"/>,
        /// used as the "unchanged" baseline. Empty when the type can't be constructed (a
        /// hand-written fixture outside the registry), which just means no highlighting.
        /// </summary>
        private Dictionary<string, string> DefaultSnapshotFor(Type t)
        {
            if (_defaultSnapshots.TryGetValue(t, out var cached)) return cached;

            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                // Never Bind()ed, so its markDirty stays null — we only ever read from it.
                var fresh = PersistentStateRegistry.Create(t);
                foreach (var fv in StateInspector.Inspect(fresh))
                {
                    try { snapshot[fv.PropertyName] = PersistenceKitWindow.ToSnapshot(fv.Get()); }
                    catch { /* a getter that throws on a default instance — skip that field */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PersistenceKit] No default baseline for {t.Name} ({ex.Message}); " +
                                 "changed-field highlighting is off for it.");
            }
            _defaultSnapshots[t] = snapshot;
            return snapshot;
        }

        private static bool HasFocusWithin(VisualElement el)
        {
            var focused = el?.focusController?.focusedElement as VisualElement;
            while (focused != null)
            {
                if (focused == el) return true;
                focused = focused.parent;
            }
            return false;
        }

        private VisualElement BuildFieldRow(StateInspector.FieldView fv)
        {
            var settings = PersistenceKitSettings.Instance;
            var row = new VisualElement();
            row.AddToClassList("la-field");

            var labelText = settings.ShowFieldTypes
                ? $"{fv.PropertyName}  ({TypeShortName(fv.FieldType)})"
                : fv.PropertyName;
            var name = new Label(labelText);
            name.AddToClassList("la-field__name");
            if (settings.ShowFieldTypes) name.tooltip = fv.FieldType.FullName;
            row.Add(name);

            // Lock glyph for encrypted fields — sits immediately to the right of the name,
            // ahead of the target chip. Encrypted leaves only encrypt at serialization time;
            // the in-memory value here is plaintext and rendered normally below.
            if (fv.Encrypted)
            {
                row.Add(PersistenceKitWindow.LockIndicator(
                    "Encrypted at rest (AES-256-CBC + HMAC-SHA256). The on-disk payload contains an enc:v1:… token, never the plaintext."));
            }

            // The group header already names the target when grouping is on; repeating it on
            // every row is just noise.
            if (settings.ShowTargetChips && !settings.GroupFieldsByTarget)
            {
                var chips = new VisualElement();
                chips.AddToClassList("la-field__chips");
                chips.Add(PersistenceKitWindow.Chip(fv.Target.ToString(), PersistenceKitWindow.TargetChipClass(fv.Target)));
                row.Add(chips);
            }

            if (fv.ReadOnly)
            {
                var ro = PersistenceKitWindow.Chip("READ-ONLY", null);
                ro.tooltip = "No public setter was generated for this field, so the inspector can't write to it.";
                row.Add(ro);
            }

            // A field routed to a store this manager never wired can't be loaded or saved.
            // Editing it would mark a dirty bit that SaveAsync throws on, so lock it and say
            // why — the edit-mode session hits this for Remote, which needs a provider only
            // the game can supply.
            bool wired = IsTargetWired(fv.Target);
            if (!wired)
            {
                var chip = PersistenceKitWindow.Chip("NO TARGET", "la-chip--encrypted");
                chip.tooltip = EditModeSession.Owns(_selManager)
                    ? $"The edit-mode session wires no {fv.Target} target — it needs a provider only your game can supply. " +
                      "Enter Play mode to inspect this field."
                    : $"This manager has no {fv.Target} target wired. Add one with PersistenceKitBuilder.UseTarget({fv.Target}, …).";
                row.Add(chip);
            }

            // An [Encrypted] field with no encryptor behind it can't be written — the payload
            // writer calls IEncryptor.Encrypt for it and NoOpEncryptor throws. That kills the
            // whole state's save, not just this field, so flag it here and disable Save Now.
            bool needsKey = fv.Encrypted && !HasEncryptor();
            if (needsKey)
            {
                var chip = PersistenceKitWindow.Chip("NO KEY", "la-chip--encrypted");
                chip.tooltip = MissingEncryptorHint();
                row.Add(chip);
            }

            var editorHost = new VisualElement();
            editorHost.AddToClassList("la-field__editor");
            row.Add(editorHost);

            var editor = BuildEditorWidget(fv, out var pullFromState);
            if (!wired || needsKey) editor.SetEnabled(false);
            editorHost.Add(editor);

            // Baseline for the changed-field tint. StateInspector walks DeclaredOnly, so every
            // field here belongs to the selected state's own type.
            var defaults = _selState != null
                ? DefaultSnapshotFor(_selState.GetType())
                : new Dictionary<string, string>();
            Func<bool> differs = null;
            if (defaults.TryGetValue(fv.PropertyName, out var defaultSnapshot))
            {
                var capturedFv = fv;
                differs = () => PersistenceKitWindow.ToSnapshot(capturedFv.Get()) != defaultSnapshot;
            }

            _fieldRefs.Add(new FieldRefreshEntry
            {
                Editor             = editor,
                Row                = row,
                PullFromState      = pullFromState,
                DiffersFromDefault = differs,
            });
            return row;
        }

        private static string TypeShortName(Type t)
        {
            // Compact "List<InventoryItem>" / "Dictionary<string, int>" forms for inline use.
            if (!t.IsGenericType) return t.Name;
            var name = t.Name;
            int back = name.IndexOf('`');
            if (back > 0) name = name.Substring(0, back);
            var args = t.GetGenericArguments();
            var sb = new StringBuilder(name);
            sb.Append('<');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeShortName(args[i]));
            }
            sb.Append('>');
            return sb.ToString();
        }

        private VisualElement BuildEditorWidget(StateInspector.FieldView fv, out Action pullFromState)
        {
            object current;
            try { current = fv.Get(); }
            catch (Exception ex)
            {
                pullFromState = () => { };
                return new Label("<error: " + ex.Message + ">");
            }

            // Encrypted fields are encrypted at serialization time only — the in-memory value
            // is plaintext, so the standard widget renders it. The lock glyph in the row's
            // header conveys the at-rest encryption.
            var ft = fv.FieldType;

            if (ft == typeof(string))
            {
                var f = new TextField { value = (string)current ?? "" };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () => f.SetValueWithoutNotify((string)SafeGet(fv) ?? "");
                return f;
            }
            if (ft == typeof(bool))
            {
                var f = new Toggle { value = (bool?)current ?? false };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () => f.SetValueWithoutNotify((bool?)SafeGet(fv) ?? false);
                return f;
            }
            if (ft == typeof(int))
            {
                var f = new IntegerField { value = (int?)current ?? 0 };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () => f.SetValueWithoutNotify((int?)SafeGet(fv) ?? 0);
                return f;
            }
            if (ft == typeof(long))
            {
                var f = new LongField { value = (long?)current ?? 0 };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () => f.SetValueWithoutNotify((long?)SafeGet(fv) ?? 0);
                return f;
            }
            if (ft == typeof(uint))
            {
                var f = new LongField { value = current == null ? 0 : Convert.ToInt64(current) };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, (uint)Math.Max(0, e.newValue)));
                pullFromState = () =>
                {
                    var v = SafeGet(fv);
                    f.SetValueWithoutNotify(v == null ? 0 : Convert.ToInt64(v));
                };
                return f;
            }
            if (ft == typeof(ulong))
            {
                var f = new TextField { value = current?.ToString() ?? "0" };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => { if (ulong.TryParse(e.newValue, out var v)) SafeSet(fv, v); });
                pullFromState = () => f.SetValueWithoutNotify(SafeGet(fv)?.ToString() ?? "0");
                return f;
            }
            if (ft == typeof(float))
            {
                var f = new FloatField { value = (float?)current ?? 0f };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () => f.SetValueWithoutNotify((float?)SafeGet(fv) ?? 0f);
                return f;
            }
            if (ft == typeof(double))
            {
                var f = new DoubleField { value = (double?)current ?? 0.0 };
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () => f.SetValueWithoutNotify((double?)SafeGet(fv) ?? 0.0);
                return f;
            }
            if (ft.IsEnum)
            {
                var f = new EnumField((Enum)(current ?? Activator.CreateInstance(ft)));
                f.SetEnabled(!fv.ReadOnly);
                f.RegisterValueChangedCallback(e => SafeSet(fv, e.newValue));
                pullFromState = () =>
                {
                    var v = SafeGet(fv);
                    if (v is Enum en) f.SetValueWithoutNotify(en);
                };
                return f;
            }

#if ODIN_INSPECTOR
            // Odin available — honour the Project Settings choice. Default forces our
            // built-in renderer; Auto/Odin both go through PropertyTree.
            if (PersistenceKitSettings.Instance.FieldRenderer != PersistenceKitSettings.InspectorRenderer.Default)
                return BuildOdinEditor(fv, out pullFromState);
#endif
            // No Odin (or settings forced built-in) — single [Serializable] reference type
            // uses Unity's stock inspector via SerializeReference + PropertyField.
            if (ft.IsClass && IsSerializable(ft) && !IsCollection(ft))
                return BuildSerializeReferenceEditor(fv, out pullFromState);
            // Collections and everything else: reflection drawer. SerializeReference fails
            // to expand generic-typed references like List<InventoryItem> in the inspector,
            // so we render those ourselves with the same look-and-feel.
            return BuildReflectionFieldEditor(fv, out pullFromState);
        }

#if ODIN_INSPECTOR
        /// <summary>
        /// Build an Odin-driven editor for a single field. Wraps the field's current value
        /// in a <see cref="PropertyTree"/> and renders it via IMGUI; the tree is recreated
        /// when the underlying reference changes (so external mutations stay in sync).
        /// </summary>
        private VisualElement BuildOdinEditor(StateInspector.FieldView fv, out Action pullFromState)
        {
            // Holder object owns the live tree + the last root we saw, so we can detect
            // reference changes coming from outside the inspector and rebuild the tree.
            var holder = new OdinTreeHolder();
            _disposables.Add(holder);

            bool hovered = false;

            var imgui = new IMGUIContainer(() =>
            {
                var current = SafeGet(fv);
                if (current == null)
                {
                    holder.DisposeTree();
                    EditorGUILayout.LabelField("<null>");
                    return;
                }
                if (!ReferenceEquals(current, holder.LastRoot))
                {
                    holder.DisposeTree();
                    holder.Tree     = PropertyTree.Create(current);
                    holder.LastRoot = current;
                }

                var tree = holder.Tree;
                if (tree == null) return;

                InspectorUtilities.BeginDrawPropertyTree(tree, false);
                tree.Draw(false);
                bool changed = tree.ApplyChanges();
                InspectorUtilities.EndDrawPropertyTree(tree);

                if (changed)
                {
                    try { fv.Set(holder.LastRoot); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            });
            imgui.style.flexGrow  = 1;
            imgui.style.minHeight = 22;

            imgui.RegisterCallback<MouseEnterEvent>(_ => hovered = true);
            imgui.RegisterCallback<MouseLeaveEvent>(_ => hovered = false);
            imgui.RegisterCallback<DetachFromPanelEvent>(_ => holder.Dispose());

            pullFromState = () =>
            {
                if (hovered) return;
                imgui.MarkDirtyRepaint();
            };
            return imgui;
        }

        private sealed class OdinTreeHolder : IDisposable
        {
            public PropertyTree Tree;
            public object       LastRoot;

            public void DisposeTree()
            {
                Tree?.Dispose();
                Tree = null;
                LastRoot = null;
            }

            public void Dispose() => DisposeTree();
        }
#endif

        private static bool IsCollection(Type t)
        {
            if (t.IsArray) return true;
            if (!t.IsGenericType) return false;
            var def = t.GetGenericTypeDefinition();
            return def == typeof(List<>)
                || def == typeof(Dictionary<,>)
                || def == typeof(HashSet<>);
        }

        private static bool IsSerializable(Type t)
            => Attribute.IsDefined(t, typeof(SerializableAttribute));

        private VisualElement BuildSerializeReferenceEditor(StateInspector.FieldView fv, out Action pullFromState)
        {
            var wrapper = ScriptableObject.CreateInstance<FieldWrapperSO>();
            wrapper.hideFlags = HideFlags.HideAndDontSave;
            wrapper.value = SafeGet(fv);
            _wrapperSOs.Add(wrapper);

            var serObj = new SerializedObject(wrapper);
            var prop   = serObj.FindProperty(nameof(FieldWrapperSO.value));

            bool hovered = false;

            var imgui = new IMGUIContainer(() =>
            {
                if (wrapper == null || prop == null) return;
                serObj.UpdateIfRequiredOrScript();

                EditorGUI.BeginChangeCheck();
                EditorGUI.indentLevel = 0;
                // Empty label — the surrounding row already prints the property name.
                EditorGUILayout.PropertyField(prop, GUIContent.none, includeChildren: true);
                if (EditorGUI.EndChangeCheck())
                {
                    serObj.ApplyModifiedPropertiesWithoutUndo();
                    try { fv.Set(wrapper.value); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            });
            imgui.style.flexGrow  = 1;
            imgui.style.minHeight = 22;

            imgui.RegisterCallback<MouseEnterEvent>(_ => hovered = true);
            imgui.RegisterCallback<MouseLeaveEvent>(_ => hovered = false);

            pullFromState = () =>
            {
                if (wrapper == null || hovered) return;
                wrapper.value = SafeGet(fv);
                serObj.UpdateIfRequiredOrScript();
                imgui.MarkDirtyRepaint();
            };

            return imgui;
        }

        private VisualElement BuildReflectionFieldEditor(StateInspector.FieldView fv, out Action pullFromState)
        {
            var drawer = new ReflectionFieldDrawer();
            var path = fv.PropertyName;
            bool hovered = false;

            var imgui = new IMGUIContainer(() =>
            {
                object current;
                try { current = fv.Get(); }
                catch (Exception ex) { EditorGUILayout.LabelField("<error: " + ex.Message + ">"); return; }

                EditorGUI.indentLevel = 0;
                if (drawer.Draw(GUIContent.none.text, current, fv.FieldType, path, out var newValue))
                {
                    try { fv.Set(newValue); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            });
            imgui.style.flexGrow = 1;
            imgui.style.minHeight = 22;

            imgui.RegisterCallback<MouseEnterEvent>(_ => hovered = true);
            imgui.RegisterCallback<MouseLeaveEvent>(_ => hovered = false);

            // The reflection drawer reads the live value every frame from fv.Get(), so
            // pullFromState is just a repaint nudge — but we still skip while hovered to
            // avoid yanking a frame mid-edit.
            pullFromState = () =>
            {
                if (hovered) return;
                imgui.MarkDirtyRepaint();
            };

            return imgui;
        }

        private void DisposeWrappers()
        {
            for (int i = 0; i < _wrapperSOs.Count; i++)
            {
                var so = _wrapperSOs[i];
                if (so != null) UnityEngine.Object.DestroyImmediate(so);
            }
            _wrapperSOs.Clear();

            for (int i = 0; i < _disposables.Count; i++)
            {
                try { _disposables[i]?.Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            _disposables.Clear();
        }

        private static object SafeGet(StateInspector.FieldView fv)
        {
            try { return fv.Get(); }
            catch { return null; }
        }

        private static string JsonUtilityOrToString(object value)
        {
            if (value == null) return "<null>";
            try { return UnityEngine.JsonUtility.ToJson(value, prettyPrint: true); }
            catch { return value.ToString(); }
        }

        private void SafeSet(StateInspector.FieldView fv, object value)
        {
            try { fv.Set(value); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        // ─── Right sidebar (Actions + Info) ──────────────────────

        /// <summary>
        /// Rebuilds the right sidebar only when the selection changes; otherwise refreshes the
        /// one label that actually moves each tick (the live dirty mask). Tearing the whole
        /// sidebar down at the refresh interval was the source of the button flicker and made
        /// the info card impossible to interact with while the loop was running.
        /// </summary>
        private void RebuildActions()
        {
            if (ReferenceEquals(_actionsBuiltForState, _selState) && _selState != null)
            {
                // Same selection — just push the live dirty value.
                if (_infoDirtyValue != null && _selManager != null)
                    _infoDirtyValue.text = _selManager.Dirty.Peek(_selState.Key).ToString();
                return;
            }
            _actionsBuiltForState = _selState;
            _infoDirtyValue = null;

            _actionsHost.Clear();
            if (_selState == null || _selManager == null)
            {
                _actionsHost.Add(PersistenceKitWindow.Hint("Pick a state to see actions here."));
                return;
            }

            var card = new VisualElement();
            card.AddToClassList("la-card");

            var title = new Label("OPERATIONS");
            title.AddToClassList("la-card__title");
            card.Add(title);

            var saveBtn = MakeButton("Save Now", () => Run(async () => await _selManager.SaveAsync(_selState)));
            var markBtn = MakeButton("Mark Dirty (all)", MarkAllDirty);

            // Saving a state that has [Encrypted] fields with no encryptor wired throws out of
            // the payload writer, so don't offer the button — an exception in the console is a
            // worse answer than a disabled control that says what's missing.
            if (NeedsEncryptorToSave(_selState))
            {
                var why = MissingEncryptorHint();
                saveBtn.SetEnabled(false);
                saveBtn.tooltip = why;
                markBtn.SetEnabled(false);
                markBtn.tooltip = why;
            }

            card.Add(saveBtn);
            card.Add(markBtn);
            card.Add(MakeButton("Delete From Storage", DeleteSelected));

            if (NeedsEncryptorToSave(_selState))
            {
                var note = new Label(MissingEncryptorHint());
                note.AddToClassList("la-note");
                note.style.whiteSpace = WhiteSpace.Normal;
                card.Add(note);
            }

            _actionsHost.Add(card);

            var info = new VisualElement();
            info.AddToClassList("la-card");
            var infoTitle = new Label("INFO");
            infoTitle.AddToClassList("la-card__title");
            info.Add(infoTitle);

            info.Add(KeyValue("Type",  _selState.GetType().Name));
            info.Add(KeyValue("Key",   _selState.Key));
            info.Add(KeyValue("Slot",  SlotOf(_selState).Length == 0 ? "<default>" : SlotOf(_selState)));
            info.Add(KeyValue("Mask",  _selState.TargetMask.ToString()));
            _infoDirtyValue = KeyValueLive("Dirty", _selManager.Dirty.Peek(_selState.Key).ToString(), info);

            _actionsHost.Add(info);
        }

        private Button MakeButton(string label, Action onClick)
        {
            var b = new Button(onClick) { text = label };
            b.AddToClassList("la-toolbar__btn");
            b.style.marginTop = 4;
            b.style.width = StyleKeyword.Auto;
            return b;
        }

        private VisualElement KeyValue(string k, string v)
        {
            var row = new VisualElement();
            row.AddToClassList("la-kv");
            var key = new Label(k);
            key.AddToClassList("la-kv__key");
            row.Add(key);
            var val = new Label(v);
            val.AddToClassList("la-kv__val");
            val.tooltip = v;
            row.Add(val);
            return row;
        }

        /// <summary>Same as <see cref="KeyValue"/> but returns the value label so callers can update it in place.</summary>
        private Label KeyValueLive(string k, string v, VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList("la-kv");
            var key = new Label(k);
            key.AddToClassList("la-kv__key");
            row.Add(key);
            var val = new Label(v);
            val.AddToClassList("la-kv__val");
            row.Add(val);
            parent.Add(row);
            return val;
        }

        /// <summary>True when the selected manager has a backing store wired for <paramref name="t"/>.</summary>
        private bool IsTargetWired(PersistTarget t)
            => _selManager != null && _selManager.Options.Targets.ContainsKey(t);

        /// <summary>
        /// True when the selected manager can actually encrypt. <see cref="NoOpEncryptor"/> is
        /// the stand-in the builder installs when nobody called <c>UseEncryptor</c>; it throws
        /// on every call rather than silently writing plaintext.
        /// </summary>
        private bool HasEncryptor()
            => _selManager != null && !(_selManager.Options.Encryptor is NoOpEncryptor);

        /// <summary>
        /// True when saving <paramref name="s"/> would throw for want of an encryptor. Note this
        /// is a property of the whole state, not one field: the serializer walks every field
        /// routed to a target, so a single unencryptable field fails the entire save.
        /// </summary>
        private bool NeedsEncryptorToSave(IPersistentState s)
        {
            if (s == null || HasEncryptor()) return false;
            var fields = StateInspector.Inspect(s);
            for (int i = 0; i < fields.Count; i++)
                if (fields[i].Encrypted) return true;
            return false;
        }

        private string MissingEncryptorHint()
            => EditModeSession.Owns(_selManager)
                ? "This state has [Encrypted] fields but no key is set, so it can't be saved. Set the same " +
                  "key your game uses under Project Settings → PersistenceKit → Edit Mode. Saving with a " +
                  "different key would produce a file your game can't decrypt."
                : "This state has [Encrypted] fields but the manager has no encryptor. Wire one with " +
                  "PersistenceKitBuilder.UseEncryptor(...).";

        private void MarkAllDirty()
        {
            // Only targets this manager can actually write. Marking one it never wired makes
            // the next SaveAsync throw instead of saving, which strands the other targets too.
            var mask = (byte)_selState.TargetMask;
            for (int i = 0; i < 4; i++)
            {
                var target = (PersistTarget)i;
                if ((mask & (1 << i)) != 0 && IsTargetWired(target))
                    _selManager.Dirty.Mark(_selState.Key, target);
            }
            UpdateTreeRowStates();
        }

        private void DeleteSelected()
        {
            var typeName = _selState.GetType().Name;
            var slot = SlotOf(_selState);
            if (!EditorUtility.DisplayDialog("Delete state",
                $"Delete {typeName}{(slot.Length == 0 ? "" : ":" + slot)} from every wired target?",
                "Delete", "Cancel"))
                return;

            var manager = _selManager;
            var stateType = _selState.GetType();
            var slotCopy = slot;
            Run(async () =>
            {
                var mi = typeof(PersistenceManager).GetMethod(nameof(PersistenceManager.DeleteAsync));
                var generic = mi.MakeGenericMethod(stateType);
                var task = (System.Threading.Tasks.ValueTask)generic.Invoke(
                    manager, new object[] { slotCopy, default(System.Threading.CancellationToken) });
                await task;
                _selState = null;
                _treeStructureSig = null;     // force tree rebuild — the cache shrunk
                Refresh();
            });
        }

        private static void Run(Func<Task> work)
        {
            async void Wrap() { try { await work(); } catch (Exception ex) { Debug.LogException(ex); } }
            Wrap();
        }

        /// <summary>
        /// The slot half of a state's <c>TypeId[:Slot]</c> key, or empty for the default slot.
        /// </summary>
        /// <remarks>
        /// Splits on the type's registered <c>TypeId</c>, not its class name — those differ
        /// whenever a state declares <c>[PersistentState(TypeId = "…")]</c>, and keying off the
        /// class name silently reported every slot of such a type as the default one.
        /// </remarks>
        private static string SlotOf(IPersistentState s)
        {
            var k = s.Key;
            var prefix = TypeIdOf(s) + ":";
            return k.StartsWith(prefix, StringComparison.Ordinal) ? k.Substring(prefix.Length) : string.Empty;
        }

        private static string TypeIdOf(IPersistentState s)
        {
            // Hand-written test fixtures aren't in the registry; their key is their class name.
            try { return PersistentStateRegistry.GetTypeId(s.GetType()); }
            catch { return s.GetType().Name; }
        }

        // ─── Overview strip ──────────────────────────────────────

        /// <summary>
        /// Summary stats above the field grid: how many states are loaded, how they're spread
        /// across targets, and how many bytes each target is holding.
        /// </summary>
        /// <remarks>
        /// The byte counts stat the disk, so this recomputes on a ~2 s cadence rather than at
        /// the tab's ~3 Hz refresh rate, and only touches the visual tree when the numbers
        /// actually move.
        /// </remarks>
        private void UpdateOverview()
        {
            if (!PersistenceKitSettings.Instance.ShowOverview)
            {
                if (_overview.childCount > 0) _overview.Clear();
                _overview.style.display = DisplayStyle.None;
                return;
            }
            _overview.style.display = DisplayStyle.Flex;

            double now = EditorApplication.timeSinceStartup;
            if (now < _overviewNextRefresh && _overviewSig != null) return;
            _overviewNextRefresh = now + OverviewIntervalSeconds;

            var managers = PersistenceManager.ActiveManagers;
            int stateCount = 0, typeCount, dirtyCount = 0;
            long saves = 0;
            var types = new HashSet<Type>();
            var perTarget = new Dictionary<PersistTarget, int>();

            foreach (var m in managers)
            {
                saves += m.SaveCount;
                foreach (var s in m.SnapshotCache())
                {
                    stateCount++;
                    types.Add(s.GetType());
                    if ((byte)m.Dirty.Peek(s.Key) != 0) dirtyCount++;
                    var mask = (byte)s.TargetMask;
                    for (int i = 0; i < 4; i++)
                        if ((mask & (1 << i)) != 0)
                        {
                            var t = (PersistTarget)i;
                            perTarget.TryGetValue(t, out var c);
                            perTarget[t] = c + 1;
                        }
                }
            }
            typeCount = types.Count;

            long bytesOnDisk = MeasureDiskBytes(managers);

            var sig = $"{stateCount}|{typeCount}|{dirtyCount}|{saves}|{bytesOnDisk}|" +
                      string.Join(",", perTarget.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
            if (sig == _overviewSig) return;
            _overviewSig = sig;

            _overview.Clear();
            _overview.Add(Stat(stateCount.ToString(), stateCount == 1 ? "state" : "states"));
            _overview.Add(Stat(typeCount.ToString(), typeCount == 1 ? "type" : "types"));
            _overview.Add(Stat(dirtyCount.ToString(), "unsaved", dirtyCount > 0 ? "la-stat--alert" : null));
            _overview.Add(Stat(saves.ToString(), "saves"));
            _overview.Add(Stat(PersistenceKitWindow.FormatBytes(bytesOnDisk), "on disk"));

            foreach (var kv in perTarget.OrderBy(p => p.Key))
                _overview.Add(Stat(kv.Value.ToString(), kv.Key.ToString().ToLowerInvariant(),
                    "la-stat--" + kv.Key.ToString().ToLowerInvariant()));
        }

        /// <summary>
        /// Total bytes sitting in the disk targets these managers are actually wired to.
        /// Zero when nothing has been written yet.
        /// </summary>
        /// <remarks>
        /// Roots come from the live target objects rather than the edit-mode settings, so the
        /// number stays truthful in Play mode, where the game's builder — not this window —
        /// decided where the files go.
        /// </remarks>
        private static long MeasureDiskBytes(List<PersistenceManager> managers)
        {
            long total = 0;
            // Two managers can share a root; count each folder once.
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in managers)
            {
                foreach (var kv in m.Options.Targets)
                {
                    string root, ext;
                    switch (kv.Value)
                    {
                        case JsonDiskTarget j:   root = j.RootDirectory; ext = ".json"; break;
                        case BinaryDiskTarget b: root = b.RootDirectory; ext = ".bin";  break;
                        default: continue;       // PlayerPrefs / Remote hold no files to measure
                    }
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    if (!counted.Add(root + "|" + ext)) continue;

                    try
                    {
                        foreach (var path in Directory.EnumerateFiles(root, "*" + ext, SearchOption.TopDirectoryOnly))
                            total += new FileInfo(path).Length;
                    }
                    catch { /* transient IO (a save mid-rename) — the next tick re-measures */ }
                }
            }
            return total;
        }

        private static VisualElement Stat(string value, string label, string variantClass = null)
        {
            var el = new VisualElement();
            el.AddToClassList("la-stat");
            if (!string.IsNullOrEmpty(variantClass)) el.AddToClassList(variantClass);
            var v = new Label(value);
            v.AddToClassList("la-stat__value");
            el.Add(v);
            var l = new Label(label.ToUpperInvariant());
            l.AddToClassList("la-stat__label");
            el.Add(l);
            return el;
        }
    }
}
