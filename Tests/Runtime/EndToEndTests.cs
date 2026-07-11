#if PERSISTENCEKIT_NEWTONSOFT
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PersistenceKit.Serializers;
using PersistenceKit.Targets;
using PersistenceKit.Tests.Fixtures;
using UnityEngine;

namespace PersistenceKit.Tests
{
    /// <summary>
    /// E2E-01: full Save → re-create-manager → Load against the real disk and PlayerPrefs
    /// targets. Equivalent to the "Mutate → Quit → Reload" gesture from the sample scene.
    /// </summary>
    [TestFixture]
    public sealed class EndToEndTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Application.temporaryCachePath, "PersistenceKit_E2E", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            PersistentStateRegistry.__ResetForTests();
            GeneratedFixtureState.__Register();
            GeneratedAllDefaultState.__Register();
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Test]
        public async Task SaveLoadRoundTrip_AcrossManagerInstances()
        {
            byte[] key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)i;
            var keyProv = new ConstantKeyProvider(key);

            // First "session": Build, mutate, save.
            using (var kit = BuildKit(keyProv, _tempRoot))
            {
                var s = await kit.LoadOrCreateAsync<GeneratedAllDefaultState>();
                s.Name  = "kobi";
                s.Level = 42;
                await kit.SaveAsync(s);
            }

            // Second "session": new manager, load, verify.
            using (var kit = BuildKit(keyProv, _tempRoot))
            {
                var s = await kit.LoadOrCreateAsync<GeneratedAllDefaultState>();
                Assert.AreEqual("kobi", s.Name);
                Assert.AreEqual(42, s.Level);
            }
        }

        [Test]
        public async Task EncryptedField_SurvivesRoundTrip_OnDisk()
        {
            byte[] key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 7);
            var keyProv = new ConstantKeyProvider(key);

            // Share remote provider + PlayerPrefs across both "sessions" so the load in
            // session 2 can see what session 1 wrote. (Disk targets share state via the
            // tempRoot already.)
            var remoteProvider = new InMemoryRemoteProvider();
            string ppKey = "pk:GeneratedFixtureState";

            try
            {
                using (var kit = BuildKit(keyProv, _tempRoot, remoteProvider))
                {
                    var s = await kit.LoadOrCreateAsync<GeneratedFixtureState>();
                    s.AuthToken    = "letmein";
                    s.UserId       = "kobi";
                    s.SessionCount = 7;
                    s.CloudTag     = "us-east";
                    await kit.SaveAsync(s);

                    // The on-disk JSON must NOT contain the plaintext "letmein".
                    var jsonPath = Path.Combine(_tempRoot, "json", "GeneratedFixtureState.json");
                    var diskJson = File.ReadAllText(jsonPath);
                    StringAssert.DoesNotContain("letmein", diskJson);
                    StringAssert.Contains("kobi", diskJson);   // un-encrypted UserId is plaintext
                }

                using (var kit = BuildKit(keyProv, _tempRoot, remoteProvider))
                {
                    var s = await kit.LoadOrCreateAsync<GeneratedFixtureState>();
                    Assert.AreEqual("letmein", s.AuthToken);
                    Assert.AreEqual("kobi",    s.UserId);
                    Assert.AreEqual(7,         s.SessionCount);
                    Assert.AreEqual("us-east", s.CloudTag);
                }
            }
            finally
            {
                PlayerPrefs.DeleteKey(ppKey);
            }
        }

        private static PersistenceManager BuildKit(IKeyProvider keys, string tempRoot, IRemotePersistenceProvider remote = null)
        {
            var jsonRoot   = Path.Combine(tempRoot, "json");
            var binaryRoot = Path.Combine(tempRoot, "binary");
            return PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json,        new JsonDiskTarget(jsonRoot))
                .UseTarget(PersistTarget.Binary,      new BinaryDiskTarget(binaryRoot))
                .UseTarget(PersistTarget.PlayerPrefs, new PlayerPrefsTarget())
                .UseTarget(PersistTarget.Remote,      new RemoteTarget(remote ?? new InMemoryRemoteProvider()))
                .UseSerializer(PersistTarget.Json,        new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.Binary,      new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.PlayerPrefs, new NewtonsoftJsonHandler())
                .UseSerializer(PersistTarget.Remote,      new NewtonsoftJsonHandler())
                .UseEncryptor(new AesGcmEncryptor(keys))
                .Build();
        }
    }
}
#endif
