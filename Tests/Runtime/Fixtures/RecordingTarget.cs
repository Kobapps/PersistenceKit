using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PersistenceKit.Tests.Fixtures
{
    /// <summary>
    /// In-memory <see cref="IPersistenceTarget"/> that records every operation. Used for
    /// per-target isolation tests: assertions check call counts and key arguments.
    /// </summary>
    public sealed class RecordingTarget : IPersistenceTarget
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new ConcurrentDictionary<string, byte[]>();

        public List<(string key, byte[] payload)> Saves   { get; } = new();
        public List<string>                        Loads   { get; } = new();
        public List<string>                        Deletes { get; } = new();

        public RecordingTarget(PersistTarget target) { Target = target; }

        public PersistTarget Target { get; }

        public ValueTask SaveAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var bytes = payload.ToArray();
            _store[key] = bytes;
            Saves.Add((key, bytes));
            return default;
        }

        public ValueTask<byte[]> LoadAsync(string key, CancellationToken ct)
        {
            Loads.Add(key);
            return _store.TryGetValue(key, out var v) ? new ValueTask<byte[]>(v) : new ValueTask<byte[]>((byte[])null);
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
        {
            Deletes.Add(key);
            _store.TryRemove(key, out _);
            return default;
        }

        public bool Exists(string key) => _store.ContainsKey(key);

        public void ResetCounters()
        {
            Saves.Clear();
            Loads.Clear();
            Deletes.Clear();
        }
    }
}
