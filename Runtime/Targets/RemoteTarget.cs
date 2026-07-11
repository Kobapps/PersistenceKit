using System;
using System.Threading;
using System.Threading.Tasks;

namespace PersistenceKit.Targets
{
    /// <summary>
    /// Persistence target that delegates to a user-supplied <see cref="IRemotePersistenceProvider"/>.
    /// The kit ships <see cref="InMemoryRemoteProvider"/> as the default stub; production wires
    /// its own (UGS / Firebase / PlayFab / custom HTTP).
    /// </summary>
    public sealed class RemoteTarget : IPersistenceTarget
    {
        private readonly IRemotePersistenceProvider _provider;

        public RemoteTarget(IRemotePersistenceProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public PersistTarget Target => PersistTarget.Remote;

        public ValueTask SaveAsync  (string key, ReadOnlyMemory<byte> payload, CancellationToken ct) => _provider.PutAsync(key, payload, ct);
        public ValueTask<byte[]> LoadAsync(string key, CancellationToken ct)                         => _provider.GetAsync(key, ct);
        public ValueTask DeleteAsync(string key, CancellationToken ct)                               => _provider.DeleteAsync(key, ct);
        public bool      Exists     (string key)                                                     => _provider.Exists(key);
    }
}
