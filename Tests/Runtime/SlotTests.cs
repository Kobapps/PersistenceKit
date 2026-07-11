using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Tests.Fixtures;

namespace PersistenceKit.Tests
{
    [TestFixture]
    public sealed class SlotTests
    {
        private RecordingTarget _json, _prefs, _remote;

        [SetUp]
        public void SetUp()
        {
            PersistentStateRegistry.__ResetForTests();
            FixtureState.__TestOnlyResetStatics();
            SoloState.__TestOnlyResetStatics();
            _json   = new RecordingTarget(PersistTarget.Json);
            _prefs  = new RecordingTarget(PersistTarget.PlayerPrefs);
            _remote = new RecordingTarget(PersistTarget.Remote);
        }

        // SLOT-01
        [Test]
        public async Task DistinctSlots_ProduceDistinctKeys()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = Build();
            var a = await kit.LoadOrCreateAsync<FixtureState>("a");
            var b = await kit.LoadOrCreateAsync<FixtureState>("b");
            Assert.AreEqual("FixtureState:a", ((IPersistentState)a).Key);
            Assert.AreEqual("FixtureState:b", ((IPersistentState)b).Key);
        }

        // SLOT-02
        [Test]
        public async Task SaveOnSlotA_DoesNotTouchSlotB()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = Build();

            var a = await kit.LoadOrCreateAsync<FixtureState>("a");
            var b = await kit.LoadOrCreateAsync<FixtureState>("b");

            a.Name = "alpha";
            await kit.SaveAsync(a);

            Assert.AreEqual(1, _json.Saves.Count);
            Assert.AreEqual("FixtureState:a", _json.Saves[0].key);
        }

        // SLOT-03
        [Test]
        public async Task DeleteOnSlotA_DoesNotDeleteSlotB()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = Build();

            await kit.LoadOrCreateAsync<FixtureState>("a");
            await kit.LoadOrCreateAsync<FixtureState>("b");

            await kit.DeleteAsync<FixtureState>("a");

            Assert.IsTrue(_json.Deletes.TrueForAll(k => k == "FixtureState:a"));
            Assert.AreEqual("FixtureState:a", _json.Deletes[0]);
            Assert.IsTrue(kit.IsLoaded<FixtureState>("b"));
            Assert.IsFalse(kit.IsLoaded<FixtureState>("a"));
        }

        // SLOT-04
        [Test]
        public async Task EmptySlot_CollapsesToBareTypeId()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = Build();

            var s = await kit.LoadOrCreateAsync<FixtureState>();
            Assert.AreEqual("FixtureState", ((IPersistentState)s).Key);
        }

        // SLOT-05 — exposed as a helper on PersistenceManager for the disk target;
        // Phase 2 verifies that distinct slots produce distinct stored payloads in the recording target.
        [Test]
        public async Task SlotsProduceIndependentPayloads()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = Build();

            var a = await kit.LoadOrCreateAsync<FixtureState>("a");
            var b = await kit.LoadOrCreateAsync<FixtureState>("b");

            a.Name = "alpha";
            b.Name = "beta";
            await kit.SaveAllAsync();

            Assert.IsTrue(_json.Exists("FixtureState:a"));
            Assert.IsTrue(_json.Exists("FixtureState:b"));
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
