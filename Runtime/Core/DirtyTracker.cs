using System;
using System.Collections.Generic;

namespace PersistenceKit
{
    /// <summary>
    /// Tracks which targets have unsaved writes for which storage keys. Backed by a
    /// <c>Dictionary&lt;string, byte&gt;</c> where the byte is bit-packed
    /// <see cref="PersistTargetMask"/>. <see cref="Take"/> swaps to <c>0</c> rather than
    /// removing the entry, so steady-state mutation does not churn the dictionary.
    /// </summary>
    public sealed class DirtyTracker
    {
        private readonly Dictionary<string, byte> _bits = new Dictionary<string, byte>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private int _dirtyCount;

        /// <summary>Raised when a key transitions from clean to dirty. Fires on the calling thread.</summary>
        public event Action<string, PersistTarget> OnDirty;

        /// <summary>Mark <paramref name="key"/> dirty for <paramref name="target"/>.</summary>
        public void Mark(string key, PersistTarget target)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            var bit = (byte)(1 << (int)target);
            bool wasClean;
            lock (_lock)
            {
                _bits.TryGetValue(key, out var current);
                wasClean = current == 0;
                var next = (byte)(current | bit);
                if (next == current) return;
                _bits[key] = next;
                if (wasClean) _dirtyCount++;
            }
            OnDirty?.Invoke(key, target);
        }

        /// <summary>Atomically read and clear the dirty mask for <paramref name="key"/>.</summary>
        public PersistTargetMask Take(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (_lock)
            {
                if (!_bits.TryGetValue(key, out var mask) || mask == 0) return PersistTargetMask.None;
                _bits[key] = 0;
                _dirtyCount--;
                return (PersistTargetMask)mask;
            }
        }

        /// <summary>Peek without clearing.</summary>
        public PersistTargetMask Peek(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (_lock)
            {
                _bits.TryGetValue(key, out var mask);
                return (PersistTargetMask)mask;
            }
        }

        /// <summary>True when at least one tracked key has a non-zero mask.</summary>
        public bool HasDirty
        {
            get { lock (_lock) return _dirtyCount > 0; }
        }

        /// <summary>Allocate a snapshot of currently-dirty keys. Used by <c>SaveAllAsync</c>.</summary>
        public List<string> SnapshotDirtyKeys()
        {
            lock (_lock)
            {
                var list = new List<string>(_dirtyCount);
                foreach (var kv in _bits)
                    if (kv.Value != 0) list.Add(kv.Key);
                return list;
            }
        }

        /// <summary>Drop all tracked state. For tests / shutdown.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _bits.Clear();
                _dirtyCount = 0;
            }
        }
    }
}
