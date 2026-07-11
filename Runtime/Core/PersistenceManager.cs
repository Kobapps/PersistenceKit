using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;

namespace PersistenceKit
{
    /// <summary>
    /// Coordinates state load / save / delete across the configured persistence targets.
    /// Caches loaded states by <c>(Type, Slot)</c> so repeat <see cref="LoadOrCreateAsync{T}"/>
    /// returns the same reference.
    /// </summary>
    public sealed class PersistenceManager : IDisposable
    {
        // Profiler markers — show up in Window → Analysis → Profiler under the categories
        // they're sampled in. Markers wrap async methods so the time captured includes the
        // I/O await; that's the right measure for "did the save block the player loop?".
        private static readonly ProfilerMarker _markerSave    = new ProfilerMarker("PersistenceKit.Save");
        private static readonly ProfilerMarker _markerSaveAll = new ProfilerMarker("PersistenceKit.SaveAll");
        private static readonly ProfilerMarker _markerLoad    = new ProfilerMarker("PersistenceKit.Load");
        private static readonly ProfilerMarker _markerDelete  = new ProfilerMarker("PersistenceKit.Delete");
        private static readonly ProfilerMarker _markerSerialize   = new ProfilerMarker("PersistenceKit.Serialize");
        private static readonly ProfilerMarker _markerDeserialize = new ProfilerMarker("PersistenceKit.Deserialize");

        // Active managers — exposed to the editor window via weak refs so a leaked window
        // doesn't keep managers alive past their natural lifetime.
        private static readonly List<WeakReference<PersistenceManager>> _activeManagers = new List<WeakReference<PersistenceManager>>();
        private static readonly object _activeLock = new object();

        /// <summary>Snapshot of currently-live managers. Convenient for the editor inspector.</summary>
        public static List<PersistenceManager> ActiveManagers
        {
            get
            {
                var list = new List<PersistenceManager>();
                lock (_activeLock)
                {
                    for (int i = _activeManagers.Count - 1; i >= 0; i--)
                    {
                        if (_activeManagers[i].TryGetTarget(out var m) && !m._disposed) list.Add(m);
                        else _activeManagers.RemoveAt(i);
                    }
                }
                return list;
            }
        }

        /// <summary>Raised after every successful Save. Used by the editor's activity log.</summary>
        public event Action<SaveEvent> OnSaved;

        private readonly PersistenceKitOptions _options;
        private readonly DirtyTracker _dirty = new DirtyTracker();
        private readonly Dictionary<StateKey, IPersistentState> _cache = new Dictionary<StateKey, IPersistentState>();
        private readonly object _cacheLock = new object();
        private bool _disposed;
        private long _saveCount;
        private long _bytesSaved;

        internal PersistenceManager(PersistenceKitOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            lock (_activeLock) _activeManagers.Add(new WeakReference<PersistenceManager>(this));
        }

        /// <summary>Dirty tracker — exposed so the optional autosave loop can subscribe.</summary>
        public DirtyTracker Dirty => _dirty;

        /// <summary>Configured options, exposed for editor inspection (read-only by convention).</summary>
        public PersistenceKitOptions Options => _options;

        /// <summary>Total number of target writes since this manager was constructed.</summary>
        public long SaveCount => _saveCount;

        /// <summary>Total bytes written across all targets since construction.</summary>
        public long BytesSaved => _bytesSaved;

        /// <summary>Snapshot of every cached state (one per type+slot loaded into this manager).</summary>
        public List<IPersistentState> SnapshotCache()
        {
            lock (_cacheLock)
            {
                var list = new List<IPersistentState>(_cache.Count);
                foreach (var s in _cache.Values) list.Add(s);
                return list;
            }
        }

        /// <summary>Payload of the <see cref="OnSaved"/> event.</summary>
        public readonly struct SaveEvent
        {
            public readonly string Key;
            public readonly PersistTarget Target;
            public readonly int Bytes;
            public readonly DateTime At;

            public SaveEvent(string key, PersistTarget target, int bytes, DateTime at)
            { Key = key; Target = target; Bytes = bytes; At = at; }
        }

        /// <summary>
        /// Load the state for <paramref name="slot"/>, instantiating a fresh one if no payload
        /// exists. Cached: subsequent calls with the same <typeparamref name="T"/> + slot return
        /// the same reference.
        /// </summary>
        public async ValueTask<T> LoadOrCreateAsync<T>(string slot = "", CancellationToken ct = default)
            where T : class, IPersistentState
        {
            ThrowIfDisposed();
            using var __pm = _markerLoad.Auto();
            slot ??= string.Empty;
            var cacheKey = new StateKey(typeof(T), slot);

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var cached)) return (T)cached;
            }

            var state = PersistentStateRegistry.Create<T>();
            BindState(state, slot);

            // Read from each target in the state's mask. Missing payloads are tolerated.
            var mask = (byte)state.TargetMask;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var target = (PersistTarget)i;
                if (!_options.Targets.TryGetValue(target, out var impl)) continue;
                var bytes = await impl.LoadAsync(state.Key, ct).ConfigureAwait(false);
                if (bytes == null) continue;
                if (!_options.Serializers.TryGetValue(target, out var handler)) continue;
                using (_markerDeserialize.Auto())
                    handler.Deserialize(bytes, state, target, _options.Encryptor);
            }

            // Drop any dirty bits set during deserialize (shouldn't happen with generated code,
            // but hand-written test fixtures may set them). Keeps load idempotent.
            _dirty.Take(state.Key);

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var raced)) return (T)raced;
                _cache[cacheKey] = state;
            }
            return state;
        }

        /// <summary>Save only the targets that have unsaved writes for <paramref name="state"/>.</summary>
        public async ValueTask SaveAsync(IPersistentState state, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (state == null) throw new ArgumentNullException(nameof(state));
            using var __pm = _markerSave.Auto();

            var dirty = (byte)_dirty.Take(state.Key);
            if (dirty == 0) return;
            var supported = (byte)state.TargetMask;
            var effective = (byte)(dirty & supported);

            // Bits we have *confirmed* were written to their backing store. Anything in
            // 'effective' that isn't here by the time we leave the method gets re-marked dirty
            // so a later save retries it — see the finally block.
            byte persisted = 0;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    if ((effective & (1 << i)) == 0) continue;
                    var target = (PersistTarget)i;
                    if (!_options.Serializers.TryGetValue(target, out var handler))
                        throw new InvalidOperationException($"No serializer wired for {target}.");
                    if (!_options.Targets.TryGetValue(target, out var impl))
                        throw new InvalidOperationException($"No target wired for {target}.");
                    ReadOnlyMemory<byte> payload;
                    using (_markerSerialize.Auto())
                        payload = handler.Serialize(state, target, _options.Encryptor);
                    await impl.SaveAsync(state.Key, payload, ct).ConfigureAwait(false);
                    // Only now is the write durable — record it as persisted *after* the await
                    // returns without throwing.
                    persisted |= (byte)(1 << i);
                    System.Threading.Interlocked.Increment(ref _saveCount);
                    System.Threading.Interlocked.Add(ref _bytesSaved, payload.Length);
                    OnSaved?.Invoke(new SaveEvent(state.Key, target, payload.Length, DateTime.UtcNow));
                }
            }
            finally
            {
                // Critical for data safety: Take() above cleared the dirty mask up front, so a
                // write that throws (disk full, IOException, remote 5xx) or a cancellation would
                // otherwise silently drop the change forever. Restore the dirty bit for every
                // target we attempted but did NOT confirm as written — including targets we
                // never reached because an earlier one threw. Mark() ORs the bit back, so this
                // composes safely with any concurrent mutation that dirtied the state again.
                byte lost = (byte)(effective & ~persisted);
                if (lost != 0)
                {
                    for (int i = 0; i < 4; i++)
                        if ((lost & (1 << i)) != 0)
                            _dirty.Mark(state.Key, (PersistTarget)i);
                }
            }
        }

        /// <summary>Save every cached state that has unsaved writes.</summary>
        public async ValueTask SaveAllAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            using var __pm = _markerSaveAll.Auto();
            List<IPersistentState> snapshot;
            lock (_cacheLock)
            {
                snapshot = new List<IPersistentState>(_cache.Count);
                foreach (var s in _cache.Values) snapshot.Add(s);
            }
            for (int i = 0; i < snapshot.Count; i++)
                await SaveAsync(snapshot[i], ct).ConfigureAwait(false);
        }

        /// <summary>Delete the state's payload from every wired target in its mask, evicting from cache.</summary>
        public async ValueTask DeleteAsync<T>(string slot = "", CancellationToken ct = default)
            where T : class, IPersistentState
        {
            ThrowIfDisposed();
            using var __pm = _markerDelete.Auto();
            slot ??= string.Empty;
            var cacheKey = new StateKey(typeof(T), slot);
            string storageKey;
            byte mask;

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var existing))
                {
                    storageKey = existing.Key;
                    mask       = (byte)existing.TargetMask;
                    _cache.Remove(cacheKey);
                }
                else
                {
                    var temp = PersistentStateRegistry.Create<T>();
                    BindState(temp, slot);
                    storageKey = temp.Key;
                    mask       = (byte)temp.TargetMask;
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var target = (PersistTarget)i;
                if (!_options.Targets.TryGetValue(target, out var impl)) continue;
                await impl.DeleteAsync(storageKey, ct).ConfigureAwait(false);
            }

            _dirty.Take(storageKey);
        }

        /// <summary>True when an instance for the given type+slot has been loaded into the cache.</summary>
        public bool IsLoaded<T>(string slot = "") where T : class, IPersistentState
        {
            slot ??= string.Empty;
            lock (_cacheLock)
                return _cache.ContainsKey(new StateKey(typeof(T), slot));
        }

        /// <summary>Drop a cached instance without touching storage.</summary>
        public void Evict<T>(string slot = "") where T : class, IPersistentState
        {
            slot ??= string.Empty;
            lock (_cacheLock)
                _cache.Remove(new StateKey(typeof(T), slot));
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private void BindState(IPersistentState state, string slot)
        {
            // One closure-per-state — we accept the allocation since it's a once-per-load cost.
            Action<PersistTarget> markDirty = target => _dirty.Mark(state.Key, target);
            state.Bind(slot, markDirty);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PersistenceManager));
        }

        private readonly struct StateKey : IEquatable<StateKey>
        {
            public readonly Type Type;
            public readonly string Slot;
            public StateKey(Type t, string s) { Type = t; Slot = s ?? string.Empty; }
            public bool Equals(StateKey other) => ReferenceEquals(Type, other.Type) && string.Equals(Slot, other.Slot, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is StateKey k && Equals(k);
            public override int GetHashCode() => unchecked((Type?.GetHashCode() ?? 0) * 397 ^ Slot.GetHashCode());
        }
    }
}
