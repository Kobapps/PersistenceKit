using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PersistenceKit.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector.Editor;
#endif

namespace PersistenceKit.Editor.Tabs
{
    /// <summary>
    /// Left sidebar: Manager → Type → Slot tree. Center: editable field grid for the
    /// selected state. Right sidebar: action card (Save / Reload / Delete) + an info card
    /// summarising target mask and dirty bits.
    /// </summary>
    /// <remarks>
    /// Refresh strategy is "rebuild only when structure changes":
    ///   - Tree: full rebuild only when the manager set or per-manager state-key set changes;
    ///     every other tick walks the cached row entries and toggles the dirty pill +
    ///     selection class in place. This kills the hover-flicker that comes from blowing
    ///     away rows during a hover.
    ///   - Field grid: full rebuild only when <c>_selState</c> changes; otherwise each editor
    ///     pulls the live value via a per-field refresh delegate, skipping any editor that
    ///     currently has focus so the user's typing isn't clobbered.
    /// </remarks>
    internal sealed class InspectorTab
    {
        private readonly ActivityLog _log;

        public VisualElement Root { get; }

        private readonly VisualElement _treeList;
        private readonly VisualElement _fieldList;
        private readonly VisualElement _actionsHost;
        private readonly Label         _selectionTitle;

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
            public Label              DirtyChip;
        }

        private sealed class FieldRefreshEntry
        {
            public VisualElement Editor;
            public Action        PullFromState;   // copies state→editor without firing change callbacks
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

            // Left sidebar: tree
            var left = new VisualElement();
            left.AddToClassList("la-sidebar");
            left.Add(PersistenceKitWindow.Heading("Loaded States"));
            var leftScroll = new ScrollView(ScrollViewMode.Vertical);
            _treeList = new VisualElement();
            leftScroll.Add(_treeList);
            left.Add(leftScroll);
            Root.Add(left);

            // Canvas: selected state's fields
            var canvas = new VisualElement();
            canvas.AddToClassList("la-canvas");
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
            RefreshTree();

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

        // ─── Tree ────────────────────────────────────────────────

        private void RefreshTree()
        {
            var managers = PersistenceManager.ActiveManagers;
            var sig = ComputeStructureSignature(managers);

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
                _treeList.Add(PersistenceKitWindow.Hint(
                    "No active PersistenceManager. Enter Play mode or call PersistenceKitBuilder.Build() somewhere to populate this view."));
                return;
            }

            for (int mi = 0; mi < managers.Count; mi++)
            {
                var manager = managers[mi];
                var managerLabel = new Label($"Manager #{mi + 1}");
                managerLabel.AddToClassList("la-heading");
                managerLabel.AddToClassList("la-heading--sub");
                _treeList.Add(managerLabel);

                var states = manager.SnapshotCache();
                var byType = new Dictionary<Type, List<IPersistentState>>();
                foreach (var s in states)
                {
                    if (!byType.TryGetValue(s.GetType(), out var list))
                        byType[s.GetType()] = list = new List<IPersistentState>();
                    list.Add(s);
                }

                if (byType.Count == 0)
                {
                    var empty = new Label("(no states loaded)");
                    empty.AddToClassList("la-note");
                    empty.AddToClassList("la-note--empty");
                    _treeList.Add(empty);
                    continue;
                }

                foreach (var kv in byType.OrderBy(p => p.Key.Name))
                {
                    var typeRow = new VisualElement();
                    typeRow.AddToClassList("la-row");
                    var typeLabel = new Label(kv.Key.Name);
                    typeLabel.AddToClassList("la-row__label");
                    typeRow.Add(typeLabel);
                    var count = new Label(kv.Value.Count.ToString());
                    count.AddToClassList("la-row__count");
                    typeRow.Add(count);
                    _treeList.Add(typeRow);

                    foreach (var s in kv.Value.OrderBy(SlotOf))
                    {
                        var row = new VisualElement();
                        row.AddToClassList("la-row");
                        row.style.marginLeft = 14;
                        var slotName = SlotOf(s);
                        var label = new Label(slotName.Length == 0 ? "<default>" : slotName);
                        label.AddToClassList("la-row__label");
                        row.Add(label);

                        // Always-present dirty chip; shown/hidden in UpdateTreeRowStates.
                        var dirty = PersistenceKitWindow.Chip("DIRTY", "la-chip--dirty");
                        dirty.style.display = DisplayStyle.None;
                        row.Add(dirty);

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
                        _treeList.Add(row);
                        _treeRows.Add(new TreeRowEntry { Manager = manager, State = s, Row = row, DirtyChip = dirty });
                    }
                }
            }

            UpdateTreeRowStates();
        }

        private void UpdateTreeRowStates()
        {
            bool showDirty = PersistenceKitSettings.Instance.ShowDirtyChips;
            foreach (var e in _treeRows)
            {
                e.Row.EnableInClassList("la-row--selected", ReferenceEquals(e.State, _selState));
                var mask = (byte)e.Manager.Dirty.Peek(e.State.Key);
                e.DirtyChip.style.display = (showDirty && mask != 0) ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private static string ComputeStructureSignature(List<PersistenceManager> managers)
        {
            // Hash of (manager identity, ordered set of state keys per manager). Rebuild
            // is triggered whenever this changes; dirty-bit changes do NOT bump the sig.
            var sb = new StringBuilder(64);
            for (int i = 0; i < managers.Count; i++)
            {
                sb.Append(managers[i].GetHashCode()).Append('@');
                var states = managers[i].SnapshotCache();
                for (int j = 0; j < states.Count; j++) sb.Append(states[j].Key).Append(',');
                sb.Append(';');
            }
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

            foreach (var fv in fields)
                _fieldList.Add(BuildFieldRow(fv));
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

            if (settings.ShowTargetChips)
            {
                var chips = new VisualElement();
                chips.AddToClassList("la-field__chips");
                chips.Add(PersistenceKitWindow.Chip(fv.Target.ToString(), PersistenceKitWindow.TargetChipClass(fv.Target)));
                row.Add(chips);
            }

            var editorHost = new VisualElement();
            editorHost.AddToClassList("la-field__editor");
            row.Add(editorHost);

            var editor = BuildEditorWidget(fv, out var pullFromState);
            editorHost.Add(editor);

            _fieldRefs.Add(new FieldRefreshEntry { Editor = editor, PullFromState = pullFromState });
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

            card.Add(MakeButton("Save Now",  () => Run(async () => await _selManager.SaveAsync(_selState))));
            card.Add(MakeButton("Mark Dirty (all)", MarkAllDirty));
            card.Add(MakeButton("Delete From Storage", DeleteSelected));

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

        private void MarkAllDirty()
        {
            var mask = (byte)_selState.TargetMask;
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0)
                    _selManager.Dirty.Mark(_selState.Key, (PersistTarget)i);
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

        private static string SlotOf(IPersistentState s)
        {
            var k = s.Key;
            var typeName = s.GetType().Name;
            if (k.StartsWith(typeName + ":", StringComparison.Ordinal)) return k.Substring(typeName.Length + 1);
            return string.Empty;
        }
    }
}
