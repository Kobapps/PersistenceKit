using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PersistenceKit.Targets
{
    /// <summary>
    /// Default <see cref="IRemotePersistenceProvider"/> for development and tests. Stores
    /// payloads in a thread-safe in-memory dictionary. Replace in production with a real
    /// backend implementation supplied via <c>PersistenceKitBuilder</c>.
    /// </summary>
    public sealed class InMemoryRemoteProvider : IRemotePersistenceProvider
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new ConcurrentDictionary<string, byte[]>();

        public ValueTask<byte[]> GetAsync(string key, CancellationToken ct)
            => _store.TryGetValue(key, out var v) ? new ValueTask<byte[]>(v) : new ValueTask<byte[]>((byte[])null);

        public ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _store[key] = data.ToArray();
            return default;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
        {
            _store.TryRemove(key, out _);
            return default;
        }

        public bool Exists(string key) => _store.ContainsKey(key);

        /// <summary>Test helper: drop every stored entry.</summary>
        public void __TestOnlyClear() => _store.Clear();
    }
}
