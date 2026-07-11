using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Tests.Fixtures;

namespace PersistenceKit.Tests
{
    [TestFixture]
    public sealed class CoreTests
    {
        [SetUp]
        public void SetUp()
        {
            PersistentStateRegistry.__ResetForTests();
            FixtureState.__TestOnlyResetStatics();
            SoloState.__TestOnlyResetStatics();
        }

        // CORE-01
        [Test]
        public void Registry_RegisterThenCreate_ReturnsFreshInstance()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            var a = PersistentStateRegistry.Create<FixtureState>();
            var b = PersistentStateRegistry.Create<FixtureState>();
            Assert.IsNotNull(a);
            Assert.IsNotNull(b);
            Assert.AreNotSame(a, b);
        }

        // CORE-02 — re-registering the same Type is idempotent. The source generator emits
        // both [InitializeOnLoadMethod] and [RuntimeInitializeOnLoadMethod], so __Register
        // runs twice on Play; we want the second call to be a silent no-op.
        [Test]
        public void Registry_DoubleRegister_SameType_IsIdempotent()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            Assert.DoesNotThrow(() =>
                PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults));
            // The first registration's factory is the one preserved.
            var instance = PersistentStateRegistry.Create<FixtureState>();
            Assert.IsNotNull(instance);
        }

        // CORE-02b — re-registering with a CONFLICTING TypeId is a real bug and still throws.
        [Test]
        public void Registry_RegisterSameType_DifferentTypeId_Throws()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults, typeId: "first");
            Assert.Throws<System.InvalidOperationException>(() =>
                PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults, typeId: "second"));
        }

        // CORE-03
        [Test]
        public async Task Registry_ResolveDefaults_InvokedOncePerTypeOnBuild()
        {
            int calls = 0;
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(),
                t => { calls++; FixtureState.ResolveDefaults(t); });

            BuildKit();
            // A second Build with the same default does not re-invoke ResolveDefaults.
            BuildKit();
            await Task.Yield();
            Assert.AreEqual(1, calls);
        }

        // CORE-04
        [Test]
        public void DirtyTracker_MarkAccumulates_TakeClears_TakeOnCleanIsNone()
        {
            var t = new DirtyTracker();
            t.Mark("k", PersistTarget.Json);
            t.Mark("k", PersistTarget.PlayerPrefs);
            Assert.AreEqual(PersistTargetMask.Json | PersistTargetMask.PlayerPrefs, t.Take("k"));
            Assert.AreEqual(PersistTargetMask.None, t.Take("k"));
        }

        // CORE-05
        [Test]
        public void DirtyTracker_ConcurrentMarks_DoNotLoseBits()
        {
            var t = new DirtyTracker();
            const int iterations = 5000;
            System.Threading.Tasks.Parallel.For(0, iterations, i =>
            {
                t.Mark("a", (PersistTarget)(i % 4));
            });
            var mask = t.Take("a");
            Assert.AreEqual(
                PersistTargetMask.Json | PersistTargetMask.Binary | PersistTargetMask.PlayerPrefs | PersistTargetMask.Remote,
                mask);
        }

        // CORE-06
        [Test]
        public async Task Manager_LoadOrCreate_CachesByTypeAndSlot()
        {
            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = BuildKit();
            var a = await kit.LoadOrCreateAsync<FixtureState>();
            var b = await kit.LoadOrCreateAsync<FixtureState>();
            Assert.AreSame(a, b);

            var c = await kit.LoadOrCreateAsync<FixtureState>("alt");
            Assert.AreNotSame(a, c);
        }

        // CORE-07
        [Test]
        public async Task Manager_Delete_EvictsAndCallsDeleteOnEveryTargetInMask()
        {
            var json = new RecordingTarget(PersistTarget.Json);
            var prefs = new RecordingTarget(PersistTarget.PlayerPrefs);
            var remote = new RecordingTarget(PersistTarget.Remote);

            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json, json)
                .UseTarget(PersistTarget.PlayerPrefs, prefs)
                .UseTarget(PersistTarget.Remote, remote)
                .UseSerializer(PersistTarget.Json,        new TestSerializer())
                .UseSerializer(PersistTarget.PlayerPrefs, new TestSerializer())
                .UseSerializer(PersistTarget.Remote,      new TestSerializer())
                .Build();

            var s = await kit.LoadOrCreateAsync<FixtureState>();
            Assert.IsTrue(kit.IsLoaded<FixtureState>());

            await kit.DeleteAsync<FixtureState>();

            Assert.IsFalse(kit.IsLoaded<FixtureState>());
            Assert.AreEqual(1, json.Deletes.Count);
            Assert.AreEqual(1, prefs.Deletes.Count);
            Assert.AreEqual(1, remote.Deletes.Count);
            Assert.AreEqual(FixtureState.TYPE_ID, json.Deletes[0]);
        }

        // CORE-08
        [Test]
        public async Task Manager_SaveAll_OnlyWritesDirty()
        {
            var json = new RecordingTarget(PersistTarget.Json);
            var prefs = new RecordingTarget(PersistTarget.PlayerPrefs);
            var remote = new RecordingTarget(PersistTarget.Remote);

            PersistentStateRegistry.Register<FixtureState>(() => new FixtureState(), FixtureState.ResolveDefaults);
            using var kit = PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json, json)
                .UseTarget(PersistTarget.PlayerPrefs, prefs)
                .UseTarget(PersistTarget.Remote, remote)
                .UseSerializer(PersistTarget.Json,        new TestSerializer())
                .UseSerializer(PersistTarget.PlayerPrefs, new TestSerializer())
                .UseSerializer(PersistTarget.Remote,      new TestSerializer())
                .Build();

            var s = await kit.LoadOrCreateAsync<FixtureState>();

            // Do not mutate — should produce zero writes.
            await kit.SaveAllAsync();
            Assert.AreEqual(0, json.Saves.Count);
            Assert.AreEqual(0, prefs.Saves.Count);
            Assert.AreEqual(0, remote.Saves.Count);

            // Dirty just one target and confirm only that target's save fires.
            s.Score = 7;
            await kit.SaveAllAsync();
            Assert.AreEqual(0, json.Saves.Count);
            Assert.AreEqual(1, prefs.Saves.Count);
            Assert.AreEqual(0, remote.Saves.Count);
        }

        private PersistenceManager BuildKit()
        {
            return PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json,        new RecordingTarget(PersistTarget.Json))
                .UseTarget(PersistTarget.PlayerPrefs, new RecordingTarget(PersistTarget.PlayerPrefs))
                .UseTarget(PersistTarget.Remote,      new RecordingTarget(PersistTarget.Remote))
                .UseSerializer(PersistTarget.Json,        new TestSerializer())
                .UseSerializer(PersistTarget.PlayerPrefs, new TestSerializer())
                .UseSerializer(PersistTarget.Remote,      new TestSerializer())
                .Build();
        }
    }
}
