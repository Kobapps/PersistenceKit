using System;

namespace PersistenceKit.Internals
{
    /// <summary>
    /// Default encryptor used until <c>PersistenceKitBuilder.UseEncryptor</c> wires a real
    /// one. Throws on any encrypt/decrypt call so accidental use of <see cref="EncryptedAttribute"/>
    /// without configuration fails loudly rather than silently storing plaintext.
    /// </summary>
    public sealed class NoOpEncryptor : IEncryptor
    {
        public static readonly NoOpEncryptor Instance = new NoOpEncryptor();

        private NoOpEncryptor() { }

        // Name the real method. This message used to point at "UseEncryption(...)", which has
        // never existed on the builder — anyone following it went looking for an API that
        // isn't there.
        private const string Message =
            "[Encrypted] field encountered but no encryptor is configured. Wire one with " +
            "PersistenceKitBuilder.UseEncryptor(new AesGcmEncryptor(new ConstantKeyProvider(key))), " +
            "where key is 32 bytes. To read or write encrypted states from the PersistenceKit " +
            "window outside Play mode, set the same key under " +
            "Project Settings → PersistenceKit → Edit Mode.";

        public string Encrypt(ReadOnlySpan<byte> plaintext, string keyPurpose)
            => throw new InvalidOperationException(Message);

        public byte[] Decrypt(string token, string keyPurpose)
            => throw new InvalidOperationException(Message);
    }
}
