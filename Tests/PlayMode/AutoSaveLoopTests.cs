using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Autosave;
using UnityEngine;
using UnityEngine.TestTools;

namespace PersistenceKit.Tests.PlayMode
{
    /// <summary>
    /// PlayMode integration tests for <see cref="AutoSaveLoop"/>. The EditMode suite covers
    /// the kit's pure-C# logic; this one drives the full timing path — debounce expiry,
    /// pause/resume, stop/start, and Install's GameObject lifecycle — against a real
    /// MonoBehaviour Update tick.
    /// </summary>
    public sealed class AutoSaveLoopTests
    {
        private const float Debounce         = 0.2f;        // short for fast tests
        private const float WaitInsideWindow = 0.10f;
        private const float WaitAfterWindow  = Debounce + 0.15f;

        // Minimal hand-written state — keeps the test asmdef self-contained.
        private sealed class TestState : IPersistentState
        {
            private static PersistTarget __t_value;
            private static PersistTargetMask __mask;

            public int Value;

            private string _slot = string.Empty;
            private Action<PersistTarget> _markDirty;

            string IPersistentState.Key => _slot.Length == 0 ? "TestState" : "TestState:" + _slot;
            PersistTargetMask IPersistentState.TargetMask => __mask;

            void IPersistentState.WritePayload(PersistTarget t, IPayloadWriter w)
            {
                if (t == __t_value) w.WriteInt32("Value", Value, false);
            }
            void IPersistentState.ReadPayload(PersistTarget t, IPayloadReader r)
            {
                if (t == __t_value && r.ReadInt32("Value", false, out var v)) Value = v;
            }
            void IPersistentState.Bind(string slot, Action<PersistTarget> markDirty)
            { _slot = slot ?? string.Empty; _markDirty = markDirty; }

            public void MarkDirty() => _markDirty?.Invoke(__t_value);
            public void MarkDirty(PersistTarget t) => _markDirty?.Invoke(t);

            public static void ResolveDefaults(PersistTarget defaultTarget)
            {
                __t_value = defaultTarget;
                __mask    = (PersistTargetMask)(1 << (int)defaultTarget);
            }

            public static void __ResetStatics() { __t_value = default; __mask = PersistTargetMask.None; }
        }

        // Inline recording target — counts saves, no need to pull the EditMode fixtures.
        private sealed class RecordingTarget : IPersistenceTarget
        {
            public PersistTarget Target { get; }
            public int SaveCount;
            public RecordingTarget(PersistTarget t) { Target = t; }
            public ValueTask SaveAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct)
            { Interlocked.Increment(ref SaveCount); return default; }
            public ValueTask<byte[]> LoadAsync(string key, CancellationToken ct) => new ValueTask<byte[]>((byte[])null);
            public ValueTask DeleteAsync(string key, CancellationToken ct) => default;
            public bool Exists(string key) => false;
        }

        // No-op serializer — we only care about save *counts*, not byte contents.
        private sealed class NoopSerializer : ISerializerHandler
        {
            public ReadOnlyMemory<byte> Serialize(IPersistentState state, PersistTarget target, IEncryptor encryptor)
                => Array.Empty<byte>();
            public void Deserialize(ReadOnlySpan<byte> payload, IPersistentState state, PersistTarget target, IEncryptor encryptor) { }
        }

        private RecordingTarget   _target;
        private PersistenceManager _kit;
        private AutoSaveLoop      _loop;
        private TestState         _state;

        [SetUp]
        public void SetUp()
        {
            PersistentStateRegistry.__ResetForTests();
            TestState.__ResetStatics();
            PersistentStateRegistry.Register<TestState>(() => new TestState(), TestState.ResolveDefaults);

            _target = new RecordingTarget(PersistTarget.Json);
            _kit = PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json, _target)
                .UseSerializer(PersistTarget.Json, new NoopSerializer())
                .Build();
        }

        [TearDown]
        public void TearDown()
        {
            if (_loop != null) UnityEngine.Object.Destroy(_loop.gameObject);
            _loop = null;
            _kit?.Dispose();
            _kit = null;
        }

        // ─── Tests ──────────────────────────────────────────────

        [UnityTest]
        public IEnumerator Autosave_Debounces_Multiple_Mutations()
        {
            _loop  = AutoSaveLoop.Install(_kit, debounceSeconds: Debounce);
            yield return LoadState();

            _state.Value = 1; _state.MarkDirty();
            _state.Value = 2; _state.MarkDirty();
            _state.Value = 3; _state.MarkDirty();

            // Half-way through the debounce window — should NOT have flushed yet.
            yield return new WaitForSecondsRealtime(WaitInsideWindow);
            Assert.AreEqual(0, _target.SaveCount, "debounce window swallowed early flush");

            // Past the window — exactly one save fires for the burst.
            yield return new WaitForSecondsRealtime(WaitAfterWindow);
            Assert.AreEqual(1, _target.SaveCount, "three mutations should collapse to one save");
        }

        [UnityTest]
        public IEnumerator Autosave_Pause_Suppresses_Saves_Until_Resume()
        {
            _loop = AutoSaveLoop.Install(_kit, debounceSeconds: Debounce);
            yield return LoadState();

            _loop.Pause();
            Assert.AreEqual(AutoSaveLoop.SyncStatus.Paused, _loop.Status);

            _state.Value = 42; _state.MarkDirty();
            yield return new WaitForSecondsRealtime(WaitAfterWindow);
            Assert.AreEqual(0, _target.SaveCount, "paused loop should not flush");

            _loop.Resume();
            Assert.AreEqual(AutoSaveLoop.SyncStatus.Active, _loop.Status);

            _state.Value = 43; _state.MarkDirty();
            yield return new WaitForSecondsRealtime(WaitAfterWindow);
            Assert.AreEqual(1, _target.SaveCount, "resumed loop should flush the next dirty event");
        }

        [UnityTest]
        public IEnumerator Autosave_Stop_HaltsLoop_Until_Start()
        {
            _loop = AutoSaveLoop.Install(_kit, debounceSeconds: Debounce);
            yield return LoadState();

            _loop.Stop();   // disables the component
            Assert.AreEqual(AutoSaveLoop.SyncStatus.Stopped, _loop.Status);

            _state.Value = 7; _state.MarkDirty();
            // Stop flushes pending state via OnDisable; the test cares about the *subsequent*
            // mutations not flushing.
            int sawAfterStop = _target.SaveCount;
            yield return new WaitForSecondsRealtime(WaitAfterWindow);
            _state.Value = 8; _state.MarkDirty();
            yield return new WaitForSecondsRealtime(WaitAfterWindow);
            Assert.AreEqual(sawAfterStop, _target.SaveCount, "stopped loop should not flush further mutations");

            _loop.StartLoop();
            Assert.AreEqual(AutoSaveLoop.SyncStatus.Active, _loop.Status);

            _state.Value = 9; _state.MarkDirty();
            yield return new WaitForSecondsRealtime(WaitAfterWindow);
            Assert.Greater(_target.SaveCount, sawAfterStop, "restarted loop should flush new dirty events");
        }

        [UnityTest]
        public IEnumerator Install_CreatesDontDestroyOnLoadComponent()
        {
            _loop = AutoSaveLoop.Install(_kit, debounceSeconds: Debounce);
            yield return null;

            Assert.IsNotNull(_loop);
            Assert.IsTrue(_loop.gameObject.scene.name == "DontDestroyOnLoad",
                "Install should mark its host GameObject DontDestroyOnLoad");
            Assert.AreNotEqual(HideFlags.None, _loop.gameObject.hideFlags & HideFlags.DontSave,
                "Install should set hideFlags so the host doesn't pollute the scene");
        }

        [UnityTest]
        public IEnumerator Quit_ForceFlushes_PendingDirty()
        {
            _loop = AutoSaveLoop.Install(_kit, debounceSeconds: 10f);     // long debounce — manual flush
            yield return LoadState();

            _state.Value = 99; _state.MarkDirty();
            yield return null;
            Assert.AreEqual(0, _target.SaveCount, "mutation should be pending behind the long debounce");

            // OnApplicationQuit/Pause flush regardless of the window. We hit OnDisable
            // directly (Destroy invokes it) which mirrors the lifecycle.
            UnityEngine.Object.Destroy(_loop.gameObject);
            _loop = null;
            // Give Unity a frame to actually destroy and flush.
            yield return null;
            yield return new WaitForSecondsRealtime(0.1f);

            Assert.AreEqual(1, _target.SaveCount, "shutdown should drain pending dirty state");
        }

        // ─── Helpers ────────────────────────────────────────────

        private IEnumerator LoadState()
        {
            // LoadOrCreateAsync over a missing payload completes synchronously — wait one
            // frame anyway so any continuation has time to land.
            var task = _kit.LoadOrCreateAsync<TestState>().AsTask();
            while (!task.IsCompleted) yield return null;
            _state = task.Result;
        }
    }
}
