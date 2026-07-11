#if PERSISTENCEKIT_NEWTONSOFT
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PersistenceKit.Internals;
using PersistenceKit.Serializers;
using PersistenceKit.Tests.Fixtures;

namespace PersistenceKit.Tests
{
    /// <summary>
    /// SER-01..04 for the Newtonsoft handler. SER-05 (encryption parity) is covered by
    /// the encryption fixture in phase 5.
    /// </summary>
    [TestFixture]
    public sealed class NewtonsoftHandlerTests
    {
        // SER-01 + SER-02 + SER-03
        [Test]
        public void RoundTrip_AllPrimitivesAndCollections()
        {
            var src = new RichFixtureState
            {
                Str   = "kobi",
                Flag  = true,
                I32   = -123,
                I64   = 9_000_000_000L,
                U32   = uint.MaxValue,
                U64   = ulong.MaxValue,
                F32   = 1.5f,
                F64   = 2.71828,
                Bytes = new byte[] { 1, 2, 3, 250 },
                Mood  = RichFixtureState.MoodKind.Happy,
                List  = new List<int> { 1, 2, 3 },
                Dict  = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
            };

            var handler = new NewtonsoftJsonHandler();
            var bytes = handler.Serialize(src, PersistTarget.Json, NoOpEncryptor.Instance);
            var dst = new RichFixtureState();
            handler.Deserialize(bytes.Span, dst, PersistTarget.Json, NoOpEncryptor.Instance);

            Assert.AreEqual(src.Str, dst.Str);
            Assert.AreEqual(src.Flag, dst.Flag);
            Assert.AreEqual(src.I32, dst.I32);
            Assert.AreEqual(src.I64, dst.I64);
            Assert.AreEqual(src.U32, dst.U32);
            Assert.AreEqual(src.U64, dst.U64);
            Assert.AreEqual(src.F32, dst.F32);
            Assert.AreEqual(src.F64, dst.F64);
            CollectionAssert.AreEqual(src.Bytes, dst.Bytes);
            Assert.AreEqual(src.Mood, dst.Mood);
            CollectionAssert.AreEqual(src.List, dst.List);
            CollectionAssert.AreEqual(src.Dict, dst.Dict);
        }

        // SER-04
        [Test]
        public void Deserialize_TolerantOfMissingFields()
        {
            var json = "{\"Str\":\"only\"}";
            var bytes = Encoding.UTF8.GetBytes(json);
            var dst = new RichFixtureState
            {
                I32 = 7,        // pre-set; missing field shouldn't disturb it
                Mood = RichFixtureState.MoodKind.Sad,
            };
            new NewtonsoftJsonHandler().Deserialize(bytes, dst, PersistTarget.Json, NoOpEncryptor.Instance);

            Assert.AreEqual("only", dst.Str);
            Assert.AreEqual(7, dst.I32);    // unchanged
            Assert.AreEqual(RichFixtureState.MoodKind.Sad, dst.Mood);
        }

        // SER-04 inverse — extra fields in payload do not throw.
        [Test]
        public void Deserialize_TolerantOfExtraFields()
        {
            var json = "{\"Str\":\"x\",\"FutureField\":42,\"AnotherFuture\":[1,2,3]}";
            var bytes = Encoding.UTF8.GetBytes(json);
            var dst = new RichFixtureState();
            Assert.DoesNotThrow(() =>
                new NewtonsoftJsonHandler().Deserialize(bytes, dst, PersistTarget.Json, NoOpEncryptor.Instance));
            Assert.AreEqual("x", dst.Str);
        }

        // Empty / fresh state round-trips cleanly (defaults preserved).
        [Test]
        public void RoundTrip_DefaultValues()
        {
            var src = new RichFixtureState();
            var handler = new NewtonsoftJsonHandler();
            var bytes = handler.Serialize(src, PersistTarget.Json, NoOpEncryptor.Instance);
            var dst = new RichFixtureState();
            handler.Deserialize(bytes.Span, dst, PersistTarget.Json, NoOpEncryptor.Instance);
            Assert.AreEqual(string.Empty, dst.Str ?? string.Empty);
            Assert.IsFalse(dst.Flag);
            Assert.AreEqual(0, dst.I32);
        }
    }
}
#endif
