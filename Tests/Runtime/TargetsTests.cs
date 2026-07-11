using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Targets;
using UnityEngine;

namespace PersistenceKit.Tests
{
    /// <summary>
    /// TGT-01..07 across each shipped target. Disk tests scope a temp directory per test;
    /// PlayerPrefs tests namespace their keys with a per-test prefix and delete them on
    /// teardown to avoid polluting the editor.
    /// </summary>
    [TestFixture]
    public sealed class TargetsTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Application.temporaryCachePath, "PersistenceKit_Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // ─── TGT-01..04 + TGT-06 for JsonDiskTarget ───
        [Test]
        public async Task JsonDisk_RoundTrip_PreservesBytes()
        {
            var t = new JsonDiskTarget(Path.Combine(_tempRoot, "json"));
            var data = MakePayload(1024);
            await t.SaveAsync("k", data, default);
            var loaded = await t.LoadAsync("k", default);
            CollectionAssert.AreEqual(data, loaded);
        }

        [Test]
        public async Task JsonDisk_LoadMissing_ReturnsNull()
        {
            var t = new JsonDiskTarget(Path.Combine(_tempRoot, "json"));
            var loaded = await t.LoadAsync("does-not-exist", default);
            Assert.IsNull(loaded);
        }

        [Test]
        public async Task JsonDisk_ExistsTracksState()
        {
            var t = new JsonDiskTarget(Path.Combine(_tempRoot, "json"));
            Assert.IsFalse(t.Exists("k"));
            await t.SaveAsync("k", MakePayload(8), default);
            Assert.IsTrue(t.Exists("k"));
            await t.DeleteAsync("k", default);
            Assert.IsFalse(t.Exists("k"));
        }

        [Test]
        public async Task JsonDisk_DeleteMissing_NoOp()
        {
            var t = new JsonDiskTarget(Path.Combine(_tempRoot, "json"));
            Assert.DoesNotThrowAsync(async () => await t.DeleteAsync("nope", default).AsTask());
            await Task.CompletedTask;
        }

        [Test]
        public async Task JsonDisk_Save_UsesAtomicTempThenReplace()
        {
            var dir = Path.Combine(_tempRoot, "json");
            var t = new JsonDiskTarget(dir);
            await t.SaveAsync("k", MakePayload(64), default);
            // After save, the .tmp scratch file must not remain alongside the final file.
            var tmp = Path.Combine(dir, "k.json.tmp");
            Assert.IsFalse(File.Exists(tmp), ".tmp file should not survive a successful save");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "k.json")));
        }

        [Test]
        public async Task JsonDisk_OverwritesExisting()
        {
            var t = new JsonDiskTarget(Path.Combine(_tempRoot, "json"));
            await t.SaveAsync("k", MakePayload(64), default);
            var newer = MakePayload(128, seed: 7);
            await t.SaveAsync("k", newer, default);
            var loaded = await t.LoadAsync("k", default);
            CollectionAssert.AreEqual(newer, loaded);
        }

        // ─── BinaryDiskTarget shares the base, so a single round-trip is enough. ───
        [Test]
        public async Task BinaryDisk_RoundTrip_PreservesBytes()
        {
            var t = new BinaryDiskTarget(Path.Combine(_tempRoot, "binary"));
            var data = MakePayload(1024);
            await t.SaveAsync("k", data, default);
            var loaded = await t.LoadAsync("k", default);
            CollectionAssert.AreEqual(data, loaded);
        }

        // ─── PlayerPrefsTarget ───
        [Test]
        public async Task PlayerPrefs_RoundTrip()
        {
            var key = "TGT_PP_" + Guid.NewGuid().ToString("N");
            try
            {
                var t = new PlayerPrefsTarget();
                var data = MakePayload(64);
                await t.SaveAsync(key, data, default);
                var loaded = await t.LoadAsync(key, default);
                CollectionAssert.AreEqual(data, loaded);
                Assert.IsTrue(t.Exists(key));
                await t.DeleteAsync(key, default);
                Assert.IsFalse(t.Exists(key));
            }
            finally
            {
                PlayerPrefs.DeleteKey("pk:" + key);
            }
        }

        [Test]
        public async Task PlayerPrefs_LoadMissing_ReturnsNull()
        {
            var t = new PlayerPrefsTarget();
            var loaded = await t.LoadAsync("does-not-exist-" + Guid.NewGuid().ToString("N"), default);
            Assert.IsNull(loaded);
        }

        // TGT-07
        [Test]
        public void PlayerPrefs_Refuses_BeyondHardLimit()
        {
            var t = new PlayerPrefsTarget(softLimitBytes: 64, hardLimitBytes: 128);
            var oversize = MakePayload(256);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await t.SaveAsync("oversize", oversize, default).AsTask());
        }

        // ─── RemoteTarget over InMemoryRemoteProvider ───
        [Test]
        public async Task Remote_RoundTrip_OverInMemoryProvider()
        {
            var provider = new InMemoryRemoteProvider();
            var t = new RemoteTarget(provider);
            var data = MakePayload(256);
            await t.SaveAsync("k", data, default);
            var loaded = await t.LoadAsync("k", default);
            CollectionAssert.AreEqual(data, loaded);
            Assert.IsTrue(t.Exists("k"));
            await t.DeleteAsync("k", default);
            Assert.IsFalse(t.Exists("k"));
            Assert.IsNull(await t.LoadAsync("k", default));
        }

        // TGT-05 — concurrent saves for the same key serialize correctly (no torn writes).
        [Test]
        public async Task JsonDisk_ConcurrentSavesSameKey_FinalContentWins()
        {
            var t = new JsonDiskTarget(Path.Combine(_tempRoot, "json"));
            var payloads = Enumerable.Range(0, 10).Select(i => MakePayload(128, seed: i)).ToArray();

            var tasks = payloads.Select(p => Task.Run(() => t.SaveAsync("k", p, default).AsTask())).ToArray();
            await Task.WhenAll(tasks);

            var loaded = await t.LoadAsync("k", default);
            // Whatever survived must match exactly one of the inputs (no torn / partial writes).
            Assert.IsTrue(payloads.Any(p => p.Length == loaded.Length && p.SequenceEqual(loaded)));
        }

        private static byte[] MakePayload(int size, int seed = 0)
        {
            var buf = new byte[size];
            var rnd = new System.Random(seed == 0 ? 1 : seed);
            rnd.NextBytes(buf);
            return buf;
        }
    }
}
