using System;
using System.Threading;
using System.Threading.Tasks;

namespace PersistenceKit
{
    /// <summary>
    /// Backend storage for a single <see cref="PersistTarget"/>.
    /// </summary>
    public interface IPersistenceTarget
    {
        /// <summary>The target this implementation services.</summary>
        PersistTarget Target { get; }

        /// <summary>Persist <paramref name="payload"/> under <paramref name="key"/>.</summary>
        ValueTask SaveAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct);

        /// <summary>Load bytes for <paramref name="key"/>, or <c>null</c> when no value is stored.</summary>
        ValueTask<byte[]> LoadAsync(string key, CancellationToken ct);

        /// <summary>Remove <paramref name="key"/>. No-op when absent.</summary>
        ValueTask DeleteAsync(string key, CancellationToken ct);

        /// <summary>Cheap existence check — must not allocate the payload.</summary>
        bool Exists(string key);
    }
}
