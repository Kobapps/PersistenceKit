using System;

namespace PersistenceKit
{
    /// <summary>
    /// Encrypts/decrypts leaf values for fields marked with <see cref="EncryptedAttribute"/>.
    /// Implementations produce a self-describing token of the form
    /// <c>"enc:&lt;version&gt;:&lt;nonce-base64&gt;:&lt;ciphertext+tag-base64&gt;"</c> so the
    /// surrounding payload format stays valid.
    /// </summary>
    public interface IEncryptor
    {
        /// <summary>Encrypt <paramref name="plaintext"/> and return a self-describing string token.</summary>
        string Encrypt(ReadOnlySpan<byte> plaintext, string keyPurpose);

        /// <summary>Decrypt a token produced by <see cref="Encrypt"/>.</summary>
        byte[] Decrypt(string token, string keyPurpose);
    }
}
