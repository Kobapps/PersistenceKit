using System;

namespace PersistenceKit.Internals
{
    /// <summary>
    /// Default encryptor used until <c>PersistenceKitBuilder.UseEncryption</c> wires a real
    /// one. Throws on any encrypt/decrypt call so accidental use of <see cref="EncryptedAttribute"/>
    /// without configuration fails loudly rather than silently storing plaintext.
    /// </summary>
    public sealed class NoOpEncryptor : IEncryptor
    {
        public static readonly NoOpEncryptor Instance = new NoOpEncryptor();

        private NoOpEncryptor() { }

        public string Encrypt(ReadOnlySpan<byte> plaintext, string keyPurpose)
            => throw new InvalidOperationException(
                "[Encrypted] field encountered but no encryptor is configured. Call PersistenceKitBuilder.UseEncryption(...).");

        public byte[] Decrypt(string token, string keyPurpose)
            => throw new InvalidOperationException(
                "[Encrypted] field encountered but no encryptor is configured. Call PersistenceKitBuilder.UseEncryption(...).");
    }
}
