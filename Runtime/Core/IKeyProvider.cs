using System;

namespace PersistenceKit
{
    /// <summary>
    /// Supplies symmetric key material for the kit's encryptor. Implementations may return
    /// different keys per <paramref name="purpose"/> to scope blast radius (e.g. tokens vs
    /// gameplay state).
    /// </summary>
    public interface IKeyProvider
    {
        /// <summary>32-byte key for AES-256-GCM. Throws if no key is configured for the purpose.</summary>
        ReadOnlySpan<byte> GetKey(string purpose);
    }
}
