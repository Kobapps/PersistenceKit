using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Tests.Fixtures;

namespace PersistenceKit.Tests
{
    [TestFixture]
    public sealed class IsolationTests
    {
        private RecordingTarget _json, _prefs, _remote;

        [SetUp]
        public void SetUp()
        {
            PersistentStateRegistry.__ResetForTests();
            FixtureState.__TestOnlyResetStatics();
            _json   = new RecordingTarget(PersistTarget.Json);
            _prefs  = new RecordingTarget(PersistTarget.PlayerPrefs);
            _remote = new RecordingTarget(PersistTarget.Remote);
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
        }

        // ISO-01
        [Test]
        public async Task MutatingOnlyOneTargetField_OnlyWritesThatTarget()
        {
            using var kit = Build();
            var s = await kit.LoadOrCreateAsync<FixtureState>();

            s.Score = 42;  // PlayerPrefs only
            await kit.SaveAsync(s);

            Assert.AreEqual(0, _json.Saves.Count);
            Assert.AreEqual(1, _prefs.Saves.Count);
            Assert.AreEqual(0, _remote.Saves.Count);
        }

        // ISO-02
        [Test]
        public async Task MutatingTwoTargets_WritesBothTargetsExactlyOnce()
        {
            using var kit = Build();
            var s = await kit.LoadOrCreateAsync<FixtureState>();

            s.Name   = "kobi";   // Json (default)
            s.Remote = "cloud";  // Remote
            await kit.SaveAsync(s);

            Assert.AreEqual(1, _json.Saves.Count);
            Assert.AreEqual(0, _prefs.Saves.Count);
            Assert.AreEqual(1, _remote.Saves.Count);
        }

        // ISO-03
        [Test]
        public async Task RepeatedMutations_BeforeSave_ProduceOneWrite()
        {
            using var kit = Build();
            var s = await kit.LoadOrCreateAsync<FixtureState>();

            s.Name = "a";
            s.Name = "b";
            s.Name = "c";
            await kit.SaveAsync(s);

            Assert.AreEqual(1, _json.Saves.Count);
        }

        private PersistenceManager Build()
        {
            return PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json,        _json)
                .UseTarget(PersistTarget.PlayerPrefs, _prefs)
                .UseTarget(PersistTarget.Remote,      _remote)
                .UseSerializer(PersistTarget.Json,        new TestSerializer())
                .UseSerializer(PersistTarget.PlayerPrefs, new TestSerializer())
                .UseSerializer(PersistTarget.Remote,      new TestSerializer())
                .Build();
        }
    }
}
