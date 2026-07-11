using System;
using System.Collections.Generic;

namespace PersistenceKit.Editor
{
    /// <summary>
    /// Fixed-capacity ring buffer of save events. The window subscribes to active managers'
    /// <see cref="PersistenceManager.OnSaved"/> hooks and pushes <see cref="Entry"/> records;
    /// the Activity tab renders the buffer in reverse-chronological order.
    /// </summary>
    internal sealed class ActivityLog
    {
        /// <summary>What produced this entry. Drives chip colour + label in the Activity tab.</summary>
        public enum EntryKind
        {
            Save             = 0,
            Export           = 1,
            Import           = 2,
            SnapshotCapture  = 3,
            SnapshotRestore  = 4,
            Reset            = 5,
        }

        /// <summary>One per-field change captured at save time. Holds enough metadata to roll the change back.</summary>
        public readonly struct FieldChange
        {
            public readonly string TypeName;      // e.g. "PlayerProfile" — used for display
            public readonly string FieldName;     // e.g. "_userId" or "UserId" — used by reflection
            public readonly string PropertyName;  // e.g. "UserId" — used for display
            public readonly string OldSnapshot;   // JSON-form of the value before the save
            public readonly string NewSnapshot;   // JSON-form of the value after the save

            public FieldChange(string typeName, string fieldName, string propertyName, string oldSnap, string newSnap)
            {
                TypeName     = typeName;
                FieldName    = fieldName;
                PropertyName = propertyName;
                OldSnapshot  = oldSnap;
                NewSnapshot  = newSnap;
            }
        }

        public readonly struct Entry
        {
            public readonly EntryKind Kind;
            public readonly DateTime  At;
            public readonly string    Key;        // Save: state key. Export/Import: file path basename.
            public readonly PersistTarget Target; // Save only — placeholder (Json) for Export/Import.
            public readonly int Bytes;
            /// <summary>Per-field changes detected for this save. Empty when none were detected.</summary>
            public readonly IReadOnlyList<FieldChange> Changes;
            /// <summary>Free-form description used by Export/Import (e.g. "4 states → file.json").</summary>
            public readonly string Description;

            public Entry(DateTime at, string key, PersistTarget target, int bytes, IReadOnlyList<FieldChange> changes = null)
            {
                Kind = EntryKind.Save;
                At = at; Key = key; Target = target; Bytes = bytes;
                Changes = changes ?? System.Array.Empty<FieldChange>();
                Description = null;
            }

            private Entry(EntryKind kind, DateTime at, string key, int bytes, string description)
            {
                Kind = kind;
                At = at; Key = key; Target = PersistTarget.Json; Bytes = bytes;
                Changes = System.Array.Empty<FieldChange>();
                Description = description;
            }

            public static Entry ForExport(DateTime at, string filename, int bytes, string description)
                => new Entry(EntryKind.Export, at, filename, bytes, description);

            public static Entry ForImport(DateTime at, string filename, int bytes, string description)
                => new Entry(EntryKind.Import, at, filename, bytes, description);

            public static Entry ForSnapshotCapture(DateTime at, string label, int bytes, string description)
                => new Entry(EntryKind.SnapshotCapture, at, label, bytes, description);

            public static Entry ForSnapshotRestore(DateTime at, string label, int bytes, string description)
                => new Entry(EntryKind.SnapshotRestore, at, label, bytes, description);

            public static Entry ForReset(DateTime at, string description)
                => new Entry(EntryKind.Reset, at, "(reset all)", 0, description);
        }

        private readonly Entry[] _buf;
        private readonly int _capacity;
        private int _next;
        private int _count;
        private long _revision;
        private readonly object _gate = new object();

        public ActivityLog(int capacity = 256)
        {
            _capacity = capacity;
            _buf = new Entry[capacity];
        }

        public int Count { get { lock (_gate) return _count; } }

        /// <summary>
        /// Monotonic counter bumped on every mutation (push/clear). The Activity tab compares
        /// this against the last value it rendered so a live-refresh tick can skip rebuilding
        /// the whole log when nothing has changed — no flicker, no wasted layout.
        /// </summary>
        public long Revision { get { lock (_gate) return _revision; } }

        public void Push(Entry e)
        {
            lock (_gate)
            {
                _buf[_next] = e;
                _next = (_next + 1) % _capacity;
                if (_count < _capacity) _count++;
                _revision++;
            }
        }

        /// <summary>Snapshot in newest-first order.</summary>
        public List<Entry> Snapshot()
        {
            lock (_gate)
            {
                var list = new List<Entry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_next - 1 - i + _capacity) % _capacity;
                    list.Add(_buf[idx]);
                }
                return list;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _next = 0;
                _count = 0;
                _revision++;
            }
        }
    }
}
