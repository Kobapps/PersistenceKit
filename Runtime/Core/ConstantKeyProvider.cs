using System;
using System.Collections.Generic;

namespace PersistenceKit
{
    /// <summary>
    /// <see cref="IKeyProvider"/> backed by a fixed mapping of purpose → 32-byte key. For
    /// production, derive keys from a user secret / device keystore rather than embedding
    /// them in the binary.
    /// </summary>
    public sealed class ConstantKeyProvider : IKeyProvider
    {
        private readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Construct with a single "default" key.</summary>
        public ConstantKeyProvider(byte[] defaultKey)
        {
            if (defaultKey == null) throw new ArgumentNullException(nameof(defaultKey));
            _keys["default"] = defaultKey;
        }

        public ConstantKeyProvider() { }

        public ConstantKeyProvider WithKey(string purpose, byte[] key)
        {
            if (string.IsNullOrEmpty(purpose)) throw new ArgumentException("purpose required", nameof(purpose));
            _keys[purpose] = key ?? throw new ArgumentNullException(nameof(key));
            return this;
        }

        public ReadOnlySpan<byte> GetKey(string purpose)
        {
            if (!_keys.TryGetValue(purpose, out var k))
                throw new InvalidOperationException($"ConstantKeyProvider: no key configured for purpose '{purpose}'.");
            return k;
        }
    }
}
