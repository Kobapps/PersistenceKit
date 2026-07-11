using System;
using System.Threading;
using System.Threading.Tasks;

namespace PersistenceKit
{
    /// <summary>
    /// User-implemented bridge to a remote backend (UGS / Firebase / PlayFab / custom HTTP).
    /// The kit ships <c>InMemoryRemoteProvider</c> for local development and tests; production
    /// builds wire their own implementation through <c>PersistenceKitBuilder.UseRemoteProvider</c>.
    /// </summary>
    public interface IRemotePersistenceProvider
    {
        ValueTask<byte[]> GetAsync(string key, CancellationToken ct);
        ValueTask         PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct);
        ValueTask         DeleteAsync(string key, CancellationToken ct);
        bool              Exists(string key);
    }
}
