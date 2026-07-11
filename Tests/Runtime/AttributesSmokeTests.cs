using System;
using NUnit.Framework;

namespace PersistenceKit.Tests
{
    [TestFixture]
    public sealed class AttributesSmokeTests
    {
        [Test]
        public void Persist_Default_UsesDefaultTarget()
        {
            var a = new PersistAttribute();
            Assert.IsTrue(a.UsesDefaultTarget);
        }

        [Test]
        public void Persist_WithTarget_BakesTarget()
        {
            var a = new PersistAttribute(PersistTarget.PlayerPrefs);
            Assert.IsFalse(a.UsesDefaultTarget);
            Assert.AreEqual(PersistTarget.PlayerPrefs, a.Target);
        }

        [Test]
        public void TargetMask_BitIndexMatchesEnum()
        {
            for (int i = 0; i < 4; i++)
            {
                var t = (PersistTarget)i;
                var expected = (PersistTargetMask)(1 << i);
                Assert.AreEqual(expected, ToMask(t), $"target {t}");
            }
        }

        [Test]
        public void Encrypted_DefaultPurpose()
        {
            var e = new EncryptedAttribute();
            Assert.AreEqual("default", e.KeyPurpose);
        }

        private static PersistTargetMask ToMask(PersistTarget t) => (PersistTargetMask)(1 << (int)t);
    }
}
