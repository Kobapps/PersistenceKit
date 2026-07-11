#if PERSISTENCEKIT_NEWTONSOFT
using System;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using PersistenceKit.Serializers;
using PersistenceKit.Tests.Fixtures;

namespace PersistenceKit.Tests
{
    /// <summary>ENC-01..07 around <see cref="AesGcmEncryptor"/>.</summary>
    [TestFixture]
    public sealed class EncryptionTests
    {
        private static readonly byte[] KeyA = MakeKey(0xAA);
        private static readonly byte[] KeyB = MakeKey(0xBB);

        private static byte[] MakeKey(byte fill)
        {
            var k = new byte[32];
            for (int i = 0; i < k.Length; i++) k[i] = (byte)(fill ^ (i * 31));
            return k;
        }

        // ENC-01
        [Test]
        public void Encrypted_NotPlaintextSubstring()
        {
            var enc = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            var token = enc.Encrypt(Encoding.UTF8.GetBytes("super-secret-token"), "default");
            StringAssert.DoesNotContain("super-secret-token", token);
        }

        // ENC-02
        [Test]
        public void RoundTrip_WithCorrectKey_Succeeds()
        {
            var enc = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            var token = enc.Encrypt(Encoding.UTF8.GetBytes("hello"), "default");
            var pt = enc.Decrypt(token, "default");
            Assert.AreEqual("hello", Encoding.UTF8.GetString(pt));
        }

        // ENC-03
        [Test]
        public void Tampering_Throws()
        {
            var enc = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            var token = enc.Encrypt(Encoding.UTF8.GetBytes("hello"), "default");
            // Flip a bit in the ciphertext segment (after the second colon).
            var lastColon = token.LastIndexOf(':');
            Assert.Greater(lastColon, 0);
            var tampered = token.Substring(0, lastColon + 1)
                + (token[lastColon + 1] == 'A' ? 'B' : 'A')
                + token.Substring(lastColon + 2);
            Assert.Throws<CryptographicException>(() => enc.Decrypt(tampered, "default"));
        }

        // ENC-04
        [Test]
        public void TwoEncryptions_OfSamePlaintext_DifferByNonce()
        {
            var enc = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            var a = enc.Encrypt(Encoding.UTF8.GetBytes("hello"), "default");
            var b = enc.Encrypt(Encoding.UTF8.GetBytes("hello"), "default");
            Assert.AreNotEqual(a, b);
        }

        // ENC-05
        [Test]
        public void WrongKey_Throws()
        {
            var encA = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            var encB = new AesGcmEncryptor(new ConstantKeyProvider(KeyB));
            var token = encA.Encrypt(Encoding.UTF8.GetBytes("hello"), "default");
            Assert.Throws<CryptographicException>(() => encB.Decrypt(token, "default"));
        }

        // ENC-06
        [Test]
        public void PerPurposeKeys_AreIsolated()
        {
            var prov = new ConstantKeyProvider().WithKey("a", KeyA).WithKey("b", KeyB);
            var enc = new AesGcmEncryptor(prov);
            var token = enc.Encrypt(Encoding.UTF8.GetBytes("hello"), "a");
            Assert.Throws<CryptographicException>(() => enc.Decrypt(token, "b"));
            Assert.AreEqual("hello", Encoding.UTF8.GetString(enc.Decrypt(token, "a")));
        }

        // ENC-07
        [Test]
        public void UnknownVersionPrefix_Throws()
        {
            var enc = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            Assert.Throws<CryptographicException>(() => enc.Decrypt("enc:v9:AAAA:BBBB", "default"));
            Assert.Throws<CryptographicException>(() => enc.Decrypt("not-a-token", "default"));
        }

        // SER-05 — encryption parity through the Newtonsoft handler.
        [Test]
        public void Newtonsoft_EncryptedString_RoundTrips()
        {
            var enc = new AesGcmEncryptor(new ConstantKeyProvider(KeyA));
            var src = new EncryptedFixtureState { Token = "letmein", Plain = "open" };
            var handler = new NewtonsoftJsonHandler();
            var bytes = handler.Serialize(src, PersistTarget.Json, enc);

            // The plaintext token must NOT appear in the bytes; the unencrypted "Plain" field
            // value must.
            var asString = Encoding.UTF8.GetString(bytes.Span);
            StringAssert.DoesNotContain("letmein", asString);
            StringAssert.Contains("open", asString);

            var dst = new EncryptedFixtureState();
            handler.Deserialize(bytes.Span, dst, PersistTarget.Json, enc);
            Assert.AreEqual("letmein", dst.Token);
            Assert.AreEqual("open", dst.Plain);
        }

        // Tiny fixture local to this test file — keeps the encryption coupling self-contained.
        private sealed class EncryptedFixtureState : IPersistentState
        {
            public string Token;
            public string Plain;
            private string _slot = string.Empty;
            private Action<PersistTarget> _markDirty;

            string IPersistentState.Key => "EncryptedFixtureState";
            PersistTargetMask IPersistentState.TargetMask => PersistTargetMask.Json;

            void IPersistentState.WritePayload(PersistTarget t, IPayloadWriter w)
            {
                if (t != PersistTarget.Json) return;
                w.WriteString("Token", Token ?? "", encrypted: true);
                w.WriteString("Plain", Plain ?? "", encrypted: false);
            }
            void IPersistentState.ReadPayload(PersistTarget t, IPayloadReader r)
            {
                if (t != PersistTarget.Json) return;
                if (r.ReadString("Token", true,  out var tk)) Token = tk;
                if (r.ReadString("Plain", false, out var p))  Plain = p;
            }
            void IPersistentState.Bind(string slot, Action<PersistTarget> m) { _slot = slot ?? ""; _markDirty = m; }
            public void MarkDirty() => _markDirty?.Invoke(PersistTarget.Json);
            public void MarkDirty(PersistTarget t) => _markDirty?.Invoke(t);
        }
    }
}
#endif
