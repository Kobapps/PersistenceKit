using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace PersistenceKit.Editor.Tabs
{
    /// <summary>
    /// Reverse-chronological tail of <see cref="PersistenceManager.OnSaved"/> events. The
    /// window subscribes once per active manager and pushes each event into a shared
    /// ring buffer; this tab paints the buffer.
    /// </summary>
    internal sealed class ActivityTab
    {
        private readonly ActivityLog _log;
        private readonly Action<ActivityLog.Entry> _rollback;
        public VisualElement Root { get; }

        private readonly ScrollView _scroll;
        private readonly Label      _summary;
        private TextField           _filter;

        // Change-detection so the live-refresh tick doesn't rebuild every row (and reset the
        // user's scroll position) several times a second when nothing has changed.
        private long   _lastRevision = -1;
        private string _lastFilter;
        private bool   _forceRebuild = true;

        public ActivityTab(ActivityLog log, Action<ActivityLog.Entry> rollback = null)
        {
            _log      = log;
            _rollback = rollback;
            Root      = new VisualElement();
            Root.AddToClassList("la-body");
            Root.style.flexGrow = 1;

            var canvas = new VisualElement();
            canvas.AddToClassList("la-canvas");

            var heading = new Label("ACTIVITY");
            heading.AddToClassList("la-heading");
            canvas.Add(heading);

            // Filter row.
            var filterRow = new VisualElement();
            filterRow.AddToClassList("la-toolbar-row");

            _filter = new TextField { value = "" };
            _filter.AddToClassList("la-search");
            _filter.style.flexGrow = 1;
            _filter.RegisterValueChangedCallback(_ => Refresh());
            var hint = new Label("filter by key/target");
            hint.AddToClassList("la-note");
            hint.style.alignSelf = Align.Center;
            hint.style.marginRight = 8;
            filterRow.Add(hint);
            filterRow.Add(_filter);
            canvas.Add(filterRow);

            _summary = new Label();
            _summary.AddToClassList("la-note");
            _summary.AddToClassList("la-note--indent");
            canvas.Add(_summary);

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.style.flexGrow = 1;
            canvas.Add(_scroll);

            Root.Add(canvas);
        }

        public void Refresh()
        {
            var query = _filter?.value;

            // Skip the rebuild entirely when neither the log nor the filter has changed —
            // this is what keeps the scroll position steady and the list flicker-free while
            // the window's live-refresh loop ticks.
            long rev = _log.Revision;
            if (!_forceRebuild && rev == _lastRevision && query == _lastFilter) return;
            _forceRebuild = false;
            _lastRevision = rev;
            _lastFilter   = query;

            _scroll.Clear();
            var entries = _log.Snapshot();
            int shown = 0;
            long bytes = 0;

            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(query))
                {
                    if (!(e.Key?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                          || e.Target.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                }

                _scroll.Add(BuildRow(e));
                shown++;
                bytes += e.Bytes;
            }

            if (shown == 0)
            {
                _scroll.Add(PersistenceKitWindow.Hint(entries.Count == 0
                    ? "No save events yet. Trigger a save (player.PropertySetter then SaveAsync) to populate this log."
                    : "No entries match the current filter."));
            }

            _summary.text = $"shown {shown} / {entries.Count}    bytes: {PersistenceKitWindow.FormatBytes(bytes)}";
        }

        private VisualElement BuildRow(ActivityLog.Entry e)
        {
            // Each entry is a small two-row card: a header row with time/target/key/bytes,
            // and an optional details block listing per-field changes when the diff turned
            // anything up.
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Column;
            card.style.marginBottom = 2;

            var header = new VisualElement();
            header.AddToClassList("la-log-row");
            card.Add(header);

            var time = new Label(e.At.ToLocalTime().ToString("HH:mm:ss.fff"));
            time.AddToClassList("la-log-row__time");
            header.Add(time);

            // Save events use the target colour; Export/Import/Snapshot get their own chip variants.
            Label chip;
            switch (e.Kind)
            {
                case ActivityLog.EntryKind.Export:
                    chip = PersistenceKitWindow.Chip("EXPORT", "la-chip--export");
                    break;
                case ActivityLog.EntryKind.Import:
                    chip = PersistenceKitWindow.Chip("IMPORT", "la-chip--import");
                    break;
                case ActivityLog.EntryKind.SnapshotCapture:
                    chip = PersistenceKitWindow.Chip("CAPTURE", "la-chip--snapshot");
                    break;
                case ActivityLog.EntryKind.SnapshotRestore:
                    chip = PersistenceKitWindow.Chip("RESTORE", "la-chip--snapshot");
                    break;
                case ActivityLog.EntryKind.Reset:
                    chip = PersistenceKitWindow.Chip("RESET", "la-chip--reset");
                    break;
                default:
                    chip = PersistenceKitWindow.Chip(e.Target.ToString(), PersistenceKitWindow.TargetChipClass(e.Target));
                    break;
            }
            header.Add(chip);

            var key = new Label(e.Kind == ActivityLog.EntryKind.Save ? e.Key : (e.Description ?? e.Key));
            key.AddToClassList("la-log-row__key");
            header.Add(key);

            var bytes = new Label(PersistenceKitWindow.FormatBytes(e.Bytes));
            bytes.AddToClassList("la-log-row__bytes");
            header.Add(bytes);

            // Per-entry rollback button — only meaningful when the diff captured something.
            if (e.Changes != null && e.Changes.Count > 0 && _rollback != null)
            {
                var capturedEntry = e;
                var undo = new Button(() => _rollback(capturedEntry)) { text = "Undo" };
                undo.AddToClassList("la-toolbar__btn");
                undo.tooltip = $"Restore the captured 'before' value for {e.Changes.Count} field(s) and trigger a fresh save.";
                undo.style.marginLeft = 4;
                undo.style.height = 18;
                undo.style.paddingTop = 0;
                undo.style.paddingBottom = 0;
                header.Add(undo);
            }

            if (e.Changes != null && e.Changes.Count > 0)
            {
                var details = new Label(BuildDetailsText(e.Changes));
                details.AddToClassList("la-log-row__details");
                details.style.whiteSpace = WhiteSpace.Normal;
                card.Add(details);
            }

            return card;
        }

        private static string BuildDetailsText(System.Collections.Generic.IReadOnlyList<ActivityLog.FieldChange> changes)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < changes.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                var c = changes[i];
                sb.Append(c.TypeName).Append('.').Append(c.PropertyName).Append(": ")
                  .Append(PersistenceKitWindow.FormatSnapshot(c.OldSnapshot))
                  .Append("  →  ")
                  .Append(PersistenceKitWindow.FormatSnapshot(c.NewSnapshot));
            }
            return sb.ToString();
        }
    }
}
