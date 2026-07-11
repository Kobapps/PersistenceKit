#if PERSISTENCEKIT_NEWTONSOFT
using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Serializers;
using PersistenceKit.Tests.Fixtures;

namespace PersistenceKit.Tests
{
    /// <summary>DEF-01..04 — default target resolution applied via the kit builder.</summary>
    [TestFixture]
    public sealed class DefaultTargetTests
    {
        // The generated __Register hooks fire on assembly load (via InitializeOnLoadMethod
        // and RuntimeInitializeOnLoadMethod). Other test fixtures call __ResetForTests in
        // their SetUp, which wipes those registrations — so we re-invoke the generator's
        // __Register here to restore them for each DEF test.
        [SetUp]
        public void SetUp()
        {
            PersistentStateRegistry.__ResetForTests();
            GeneratedFixtureState.__Register();
            GeneratedAllDefaultState.__Register();
        }

        // DEF-02 — default-target falls back to Json when the builder doesn't set one.
        [Test]
        public async Task NoUseDefaultTarget_DefaultsToJson()
        {
            using var kit = PersistenceKitBuilder.Default()
                .UseTarget(PersistTarget.Json,        new RecordingTarget(PersistTarget.Json))
                .UseTarget(PersistTarget.PlayerPrefs, new RecordingTarget(PersistTarget.PlayerPrefs))
                .UseTarget(PersistTarget.Remote,      new RecordingTarget(PersistTarget.Remote))
                .UseSerializer(PersistTarget.Json,        new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.PlayerPrefs, new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.Remote,      new NewtonsoftJsonHandler())
                .Build();

            var s = await kit.LoadOrCreateAsync<GeneratedAllDefaultState>();
            // After Build, the registry has resolved defaults to Json.
            // The state's mask must include Json.
            var mask = (byte)((IPersistentState)s).TargetMask;
            Assert.AreNotEqual(0, mask & (byte)PersistTargetMask.Json);
        }

        // DEF-03 — calling ResolveDefaults twice with different defaults is rejected.
        [Test]
        public void DoubleResolve_DifferentDefault_Throws()
        {
            // First resolve happens during the manager build above (or in earlier tests).
            // Trying to resolve to a different target now must throw.
            // If the registry hasn't been resolved yet (test ordering), this resolves once
            // first, then asserts the second resolve throws.
            try { PersistentStateRegistry.ResolveDefaults(PersistTarget.Json); } catch { /* may already be resolved */ }
            Assert.Throws<System.InvalidOperationException>(() =>
                PersistentStateRegistry.ResolveDefaults(PersistTarget.Binary));
        }

        // DEF-04 — explicit-target fields ignore the default; default-target fields follow it.
        [Test]
        public async Task MixedTargets_RespectExplicitOverDefault()
        {
            var jsonRec   = new RecordingTarget(PersistTarget.Json);
            var prefsRec  = new RecordingTarget(PersistTarget.PlayerPrefs);
            var remoteRec = new RecordingTarget(PersistTarget.Remote);

            using var kit = PersistenceKitBuilder.Default()
                .UseTarget(PersistTarget.Json,        jsonRec)
                .UseTarget(PersistTarget.PlayerPrefs, prefsRec)
                .UseTarget(PersistTarget.Remote,      remoteRec)
                .UseSerializer(PersistTarget.Json,        new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.PlayerPrefs, new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.Remote,      new NewtonsoftJsonHandler())
                .UseEncryptor(new AesGcmEncryptor(new ConstantKeyProvider(new byte[32])))
                .Build();

            var s = await kit.LoadOrCreateAsync<GeneratedFixtureState>();

            // Mutate the bare-[Persist] field — must hit Json.
            s.UserId = "abc";
            await kit.SaveAsync(s);
            Assert.AreEqual(1, jsonRec.Saves.Count, "default-target field should land in Json");
            Assert.AreEqual(0, prefsRec.Saves.Count);
            Assert.AreEqual(0, remoteRec.Saves.Count);

            jsonRec.ResetCounters();

            // Mutate the explicit-Remote field — must hit Remote, not Json.
            s.CloudTag = "us-east";
            await kit.SaveAsync(s);
            Assert.AreEqual(0, jsonRec.Saves.Count);
            Assert.AreEqual(1, remoteRec.Saves.Count);

            remoteRec.ResetCounters();

            // Mutate the explicit-PlayerPrefs field.
            s.SessionCount = 7;
            await kit.SaveAsync(s);
            Assert.AreEqual(0, jsonRec.Saves.Count);
            Assert.AreEqual(0, remoteRec.Saves.Count);
            Assert.AreEqual(1, prefsRec.Saves.Count);
        }
    }
}
#endif
