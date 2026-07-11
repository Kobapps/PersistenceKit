using System;
using System.IO;
using System.Security.Cryptography;
using Unity.Profiling;

namespace PersistenceKit
{
    /// <summary>
    /// Authenticated encryption using AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC). Tokens
    /// look like <c>"enc:v1:&lt;iv-base64&gt;:&lt;ct-base64&gt;:&lt;tag-base64&gt;"</c>. A fresh
    /// 16-byte IV is generated per write so identical plaintexts yield different ciphertexts;
    /// the HMAC tag is verified before decryption, so tampering surfaces as
    /// <see cref="CryptographicException"/>.
    /// </summary>
    /// <remarks>
    /// We pair AES-CBC with HMAC because Unity's Mono runtime does not ship
    /// <see cref="AesGcm"/> on every platform — its constructor throws
    /// <see cref="PlatformNotSupportedException"/>. CBC+HMAC uses primitives the BCL
    /// supports everywhere and matches AES-GCM's security guarantees against an offline
    /// attacker (the kit's threat model). The class name is kept for ABI stability; the
    /// xmldoc above is the source of truth on the algorithm.
    /// </remarks>
    public sealed class AesGcmEncryptor : IEncryptor, IDisposable
    {
        private const string Prefix    = "enc:v1:";
        private const int    IvSize    = 16;   // AES block size
        private const int    TagSize   = 32;   // HMAC-SHA256 output

        private static readonly ProfilerMarker _markerEncrypt = new ProfilerMarker("PersistenceKit.Encrypt");
        private static readonly ProfilerMarker _markerDecrypt = new ProfilerMarker("PersistenceKit.Decrypt");

        private readonly IKeyProvider _keys;
        private bool _disposed;

        public AesGcmEncryptor(IKeyProvider keys)
        {
            _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        }

        public string Encrypt(ReadOnlySpan<byte> plaintext, string keyPurpose)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AesGcmEncryptor));
            using var __pm = _markerEncrypt.Auto();

            var master = _keys.GetKey(keyPurpose);
            if (master.Length != 32)
                throw new InvalidOperationException(
                    $"AesGcmEncryptor expects a 32-byte master key for purpose '{keyPurpose}', got {master.Length}.");

            var (ke, km) = DeriveKeys(master);

            var iv = new byte[IvSize];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(iv);

            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key     = ke;
                aes.IV      = iv;
                using var enc = aes.CreateEncryptor();
                using var ms  = new MemoryStream();
                using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                {
                    var pt = plaintext.ToArray();
                    cs.Write(pt, 0, pt.Length);
                    cs.FlushFinalBlock();
                }
                ciphertext = ms.ToArray();
            }

            byte[] tag;
            using (var hmac = new HMACSHA256(km))
            {
                hmac.TransformBlock(iv, 0, iv.Length, null, 0);
                hmac.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                tag = hmac.Hash;
            }

            return Prefix
                 + Convert.ToBase64String(iv)         + ":"
                 + Convert.ToBase64String(ciphertext) + ":"
                 + Convert.ToBase64String(tag);
        }

        public byte[] Decrypt(string token, string keyPurpose)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AesGcmEncryptor));
            using var __pm = _markerDecrypt.Auto();

            if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
                throw new CryptographicException("Token is not a valid PersistenceKit encryption token.");

            var rest  = token.Substring(Prefix.Length);
            var parts = rest.Split(':');
            if (parts.Length != 3)
                throw new CryptographicException("Malformed encryption token (need iv:ct:tag).");

            byte[] iv, ct, tag;
            try
            {
                iv  = Convert.FromBase64String(parts[0]);
                ct  = Convert.FromBase64String(parts[1]);
                tag = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("Encryption token contains invalid base64.", ex);
            }

            if (iv.Length != IvSize)
                throw new CryptographicException("Encryption token IV has wrong length.");
            if (tag.Length != TagSize)
                throw new CryptographicException("Encryption token tag has wrong length.");

            var master = _keys.GetKey(keyPurpose);
            if (master.Length != 32)
                throw new InvalidOperationException(
                    $"AesGcmEncryptor expects a 32-byte master key for purpose '{keyPurpose}', got {master.Length}.");

            var (ke, km) = DeriveKeys(master);

            byte[] expected;
            using (var hmac = new HMACSHA256(km))
            {
                hmac.TransformBlock(iv, 0, iv.Length, null, 0);
                hmac.TransformFinalBlock(ct, 0, ct.Length);
                expected = hmac.Hash;
            }
            if (!ConstantTimeEquals(expected, tag))
                throw new CryptographicException("Authentication failed (wrong key or tampered ciphertext).");

            using (var aes = Aes.Create())
            {
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key     = ke;
                aes.IV      = iv;
                using var dec = aes.CreateDecryptor();
                return dec.TransformFinalBlock(ct, 0, ct.Length);
            }
        }

        // Derive separate encryption (Ke) and MAC (Km) keys from the master so neither
        // primitive sees the raw key material. SHA-256 is a HKDF-stand-in here — adequate
        // for a 32-byte high-entropy master; for a low-entropy passphrase, run PBKDF2 first.
        private static (byte[] ke, byte[] km) DeriveKeys(ReadOnlySpan<byte> master)
        {
            var buf = new byte[master.Length + 1];
            master.CopyTo(buf);
            using var sha = SHA256.Create();
            buf[master.Length] = 0x01;
            var ke = sha.ComputeHash(buf);
            buf[master.Length] = 0x02;
            var km = sha.ComputeHash(buf);
            return (ke, km);
        }

        // Constant-time comparison — required to avoid timing oracles on the tag.
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void Dispose() => _disposed = true;
    }
}
