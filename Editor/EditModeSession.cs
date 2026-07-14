using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PersistenceKit.Editor.Settings;
using PersistenceKit.Targets;
using UnityEditor;
using UnityEngine;
#if PERSISTENCEKIT_NEWTONSOFT
using PersistenceKit.Serializers;
#endif

namespace PersistenceKit.Editor
{
    /// <summary>
    /// An editor-owned <see cref="PersistenceManager"/> that makes saved states readable and
    /// editable while the game is <i>not</i> running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outside Play mode nothing calls <c>PersistenceKitBuilder.Build()</c>, so
    /// <see cref="PersistenceManager.ActiveManagers"/> is empty and every tab in the window
    /// has nothing to show. The type <i>registry</i>, however, is populated: the generator
    /// emits an <c>[InitializeOnLoadMethod]</c> registration hook alongside the runtime one,
    /// so every <c>[PersistentState]</c> type registers itself on each domain reload. This
    /// session enumerates those types and loads their payloads through a manager it builds
    /// itself — which is why the existing tabs light up unchanged: the edit-mode manager
    /// registers into <c>ActiveManagers</c> like any other.
    /// </para>
    /// <para>
    /// The wiring is guesswork by necessity — the game's builder lives in user code the editor
    /// can't see — so targets, roots, the default target and the encryption key all come from
    /// Project Settings → PersistenceKit. Defaults match the targets' own default constructors,
    /// which is what an unconfigured game uses.
    /// </para>
    /// <para><b>Why this bothers to restore statics.</b> Reading a state's
    /// <see cref="IPersistentState.TargetMask"/> requires defaults to be resolved, and
    /// <c>ResolveDefaults</c> is a process-wide latch: it throws if a later caller picks a
    /// different target, and the generated <c>__ResolveDefaults</c> only ever ORs bits into a
    /// type's mask, never clears them. With the default Enter Play Mode settings the domain
    /// reloads and all of this evaporates — but with the reload disabled our provisional
    /// resolution would outlive us and either throw inside the game's <c>Build()</c> or leave
    /// a stray target bit that makes the game write to a store it never wired. So the session
    /// snapshots each type's mask before resolving and restores it (plus releases the latch)
    /// on the way out.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class EditModeSession
    {
        /// <summary>Name of the private static mask field the generator emits per state type.</summary>
        private const string MaskFieldName = "__mask";

        private static PersistenceManager _manager;

        // Per-type snapshot of __mask taken immediately before we resolved defaults.
        private static readonly Dictionary<FieldInfo, object> _maskBackup = new Dictionary<FieldInfo, object>();

        // True when *we* were the ones who latched the registry's resolved default, and are
        // therefore the ones who should release it.
        private static bool _weResolvedDefaults;

        private static readonly MethodInfo _loadOrCreateAsync =
            typeof(PersistenceManager).GetMethod(nameof(PersistenceManager.LoadOrCreateAsync));

        static EditModeSession()
        {
            EditorApplication.playModeStateChanged  += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }

        /// <summary>The edit-mode manager, or null when no session is running.</summary>
        public static PersistenceManager Manager => _manager;

        /// <summary>True while an edit-mode session owns a live manager.</summary>
        public static bool IsRunning => _manager != null;

        /// <summary>True when <paramref name="manager"/> is this session's, not the game's.</summary>
        public static bool Owns(PersistenceManager manager)
            => manager != null && ReferenceEquals(manager, _manager);

        /// <summary>Last Start/Load failure, surfaced by the window. Null when healthy.</summary>
        public static string LastError { get; private set; }

        /// <summary>Per-state load failures from the last <see cref="LoadAllAsync"/>, keyed by state key.</summary>
        public static IReadOnlyDictionary<string, string> LoadErrors => _loadErrors;
        private static readonly Dictionary<string, string> _loadErrors = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// False when no serializer assembly is present, which makes an edit-mode session
        /// impossible — the kit can't turn a payload back into a state without one.
        /// </summary>
        public static bool SerializerAvailable =>
#if PERSISTENCEKIT_NEWTONSOFT
            true;
#else
            false;
#endif

        /// <summary>Number of <c>[PersistentState]</c> types registered in this domain.</summary>
        public static int RegisteredTypeCount => PersistentStateRegistry.Snapshot().Count;

        /// <summary>
        /// True when a key is configured and the session can read/write <c>[Encrypted]</c>
        /// fields. False means those states load and save as errors, not silently as plaintext.
        /// </summary>
        public static bool HasEncryptor =>
            _manager != null && !(_manager.Options.Encryptor is Internals.NoOpEncryptor);

        /// <summary>
        /// True when the project declares <c>[Encrypted]</c> fields anywhere. Lets the window
        /// nag about a missing key only when it would actually bite.
        /// </summary>
        public static bool AnyEncryptedFields()
        {
            foreach (var rs in PersistentStateRegistry.Snapshot())
            {
                var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var f in rs.Type.GetFields(bf))
                    if (f.IsDefined(typeof(EncryptedAttribute), inherit: false)) return true;
            }
            return false;
        }

        /// <summary>A fresh 32-byte key, Base64-encoded, for the settings field.</summary>
        public static string GenerateKeyBase64()
        {
            var key = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(key);
            return Convert.ToBase64String(key);
        }

        // ─── Lifecycle ───────────────────────────────────────────

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Tear down before the game gets a chance to build its own manager. With domain
            // reload on this is redundant (the reload wipes us anyway); with it off, this is
            // the only thing standing between our provisional resolution and the game's Build().
            if (change == PlayModeStateChange.ExitingEditMode) Stop();
        }

        /// <summary>
        /// Build the edit-mode manager. No-op when one is already running. Returns false and
        /// sets <see cref="LastError"/> on failure.
        /// </summary>
        public static bool Start()
        {
            if (_manager != null) return true;
            LastError = null;
            _loadErrors.Clear();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                LastError = "Play mode is starting — the game builds its own manager.";
                return false;
            }
#if !PERSISTENCEKIT_NEWTONSOFT
            LastError = "No serializer available. Install com.unity.nuget.newtonsoft-json to read saved states outside Play mode.";
            return false;
#else
            try
            {
                var settings = PersistenceKitSettings.Instance;

                // If something already resolved defaults this domain, adopt that target rather
                // than fighting it — ResolveDefaults throws on a conflicting value, and the
                // existing resolution is by definition the one the states' masks were built for.
                bool alreadyResolved = PersistentStateRegistry.TryGetResolvedDefault(out var resolved);
                var defaultTarget = alreadyResolved ? resolved : settings.EditModeDefaultTarget;

                // Must happen before Build() — that's what resolves defaults and ORs the masks.
                BackupMasks();

                var handler = new NewtonsoftJsonHandler(indent: true);
                var builder = PersistenceKitBuilder.Default()
                    .UseDefaultTarget(defaultTarget)
                    .UseTarget(PersistTarget.Json, MakeDiskTarget(settings.EditModeJsonRoot, json: true))
                    .UseSerializer(PersistTarget.Json, handler)
                    .UseTarget(PersistTarget.Binary, MakeDiskTarget(settings.EditModeBinaryRoot, json: false))
                    .UseSerializer(PersistTarget.Binary, handler)
                    .UseTarget(PersistTarget.PlayerPrefs, new PlayerPrefsTarget())
                    .UseSerializer(PersistTarget.PlayerPrefs, handler);

                // Remote is deliberately unwired: it needs an IRemotePersistenceProvider only the
                // game can supply, and an in-memory stub would just render a convincing lie
                // (always empty). Remote-routed fields simply don't load here.

                var encryptor = TryMakeEncryptor(settings, out var encError);
                if (encryptor != null) builder.UseEncryptor(encryptor);
                else if (encError != null) Debug.LogWarning("[PersistenceKit] " + encError);

                _manager = builder.Build();
                _weResolvedDefaults = !alreadyResolved;
                return true;
            }
            catch (Exception ex)
            {
                // Build() threw — we may have already perturbed the masks; put them back.
                RestoreMasks();
                _manager = null;
                LastError = ex.Message;
                Debug.LogException(ex);
                return false;
            }
#endif
        }

        /// <summary>
        /// Dispose the edit-mode manager and undo the process-wide state it touched. Safe to
        /// call when no session is running.
        /// </summary>
        public static void Stop()
        {
            if (_manager == null)
            {
                // Masks may still be backed up if Start() failed between backup and Build().
                RestoreMasks();
                return;
            }

            try { _manager.Dispose(); }
            catch (Exception ex) { Debug.LogException(ex); }
            _manager = null;
            _loadErrors.Clear();

            RestoreMasks();
            if (_weResolvedDefaults)
            {
                PersistentStateRegistry.__UnresolveDefaults();
                _weResolvedDefaults = false;
            }
        }

        /// <summary>Stop, restart, and reload every saved state from storage.</summary>
        public static async Task ReloadAsync()
        {
            Stop();
            if (!Start()) return;
            await LoadAllAsync();
        }

        // ─── Loading ─────────────────────────────────────────────

        /// <summary>
        /// Load every registered state's default slot, plus any named slots discoverable on
        /// disk, into the edit-mode manager's cache. Per-type failures are recorded in
        /// <see cref="LoadErrors"/> rather than aborting the sweep.
        /// </summary>
        public static async Task<(int loaded, int failed)> LoadAllAsync()
        {
            if (_manager == null && !Start()) return (0, 0);
            _loadErrors.Clear();

            var registered = PersistentStateRegistry.Snapshot();
            var slotsByTypeId = DiscoverSlots(registered);

            int loaded = 0, failed = 0;
            foreach (var rs in registered)
            {
                if (!slotsByTypeId.TryGetValue(rs.TypeId, out var slots)) continue;
                foreach (var slot in slots)
                {
                    // A manager disposed mid-sweep (user hit Play) would throw on every
                    // remaining type; stop cleanly instead.
                    if (_manager == null) return (loaded, failed);
                    try
                    {
                        await LoadOneAsync(_manager, rs.Type, slot);
                        loaded++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        var key = slot.Length == 0 ? rs.TypeId : rs.TypeId + ":" + slot;
                        _loadErrors[key] = Unwrap(ex).Message;
                    }
                }
            }
            return (loaded, failed);
        }

        /// <summary>Load a single type+slot into the session, e.g. for a slot the user names by hand.</summary>
        public static async Task<bool> LoadSlotAsync(Type stateType, string slot)
        {
            if (_manager == null && !Start()) return false;
            try
            {
                await LoadOneAsync(_manager, stateType, slot ?? string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                LastError = Unwrap(ex).Message;
                Debug.LogException(Unwrap(ex));
                return false;
            }
        }

        private static Task LoadOneAsync(PersistenceManager manager, Type stateType, string slot)
        {
            if (_loadOrCreateAsync == null)
                throw new InvalidOperationException("PersistenceManager.LoadOrCreateAsync not found — kit version mismatch.");

            var generic = _loadOrCreateAsync.MakeGenericMethod(stateType);
            var valueTask = generic.Invoke(manager, new object[] { slot, default(CancellationToken) });

            // ValueTask<T> boxed as object — AsTask() gives us something awaitable without
            // knowing T at compile time.
            var asTask = valueTask.GetType().GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public);
            if (asTask == null)
                throw new InvalidOperationException("ValueTask<T>.AsTask() not found — unexpected runtime.");
            return (Task)asTask.Invoke(valueTask, null);
        }

        /// <summary>
        /// Reflection through <c>MethodInfo.Invoke</c> wraps anything the target throws in a
        /// <see cref="TargetInvocationException"/>; report what actually went wrong.
        /// </summary>
        private static Exception Unwrap(Exception ex)
            => ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;

        /// <summary>
        /// Map each registered type id to the slots worth loading: always the default slot,
        /// plus any named slot we can see on disk.
        /// </summary>
        /// <remarks>
        /// Slot names are recovered from filenames, which the disk target sanitises by
        /// replacing the <c>TypeId:Slot</c> separator with '_'. That is lossy — a file named
        /// <c>Player_1</c> is both "type id Player, slot 1" and "type id Player_1, default
        /// slot". We resolve it by preferring the longest registered type id that matches,
        /// so an actual <c>Player_1</c> type wins over a slot of <c>Player</c>. PlayerPrefs
        /// and Remote expose no enumeration API, so named slots living only there aren't
        /// discoverable — load those by name with <see cref="LoadSlotAsync"/>.
        /// </remarks>
        private static Dictionary<string, HashSet<string>> DiscoverSlots(
            List<PersistentStateRegistry.RegisteredState> registered)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var rs in registered)
                result[rs.TypeId] = new HashSet<string>(StringComparer.Ordinal) { string.Empty };

            var ids = new List<string>(result.Keys);
            ids.Sort((a, b) => b.Length.CompareTo(a.Length));   // longest first

            foreach (var (root, ext) in DiskRoots())
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] files;
                try { files = Directory.GetFiles(root, "*" + ext, SearchOption.TopDirectoryOnly); }
                catch (Exception ex) { Debug.LogWarning($"[PersistenceKit] Couldn't scan '{root}': {ex.Message}"); continue; }

                foreach (var path in files)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(name)) continue;
                    foreach (var id in ids)
                    {
                        if (string.Equals(name, id, StringComparison.Ordinal))
                            break;                                    // default slot — already seeded
                        if (name.Length > id.Length + 1
                            && name.StartsWith(id, StringComparison.Ordinal)
                            && name[id.Length] == '_')
                        {
                            result[id].Add(name.Substring(id.Length + 1));
                            break;
                        }
                    }
                }
            }
            return result;
        }

        // ─── Wiring helpers ──────────────────────────────────────

        /// <summary>
        /// The (root, extension) pairs this session's disk targets will read from, derived from
        /// settings. Used to find slots before a manager exists; once one does, ask its target
        /// objects for their real roots instead.
        /// </summary>
        private static IEnumerable<(string root, string ext)> DiskRoots()
        {
            var settings = PersistenceKitSettings.Instance;
            yield return (ResolveRoot(settings.EditModeJsonRoot) ?? DefaultRoot("json"), ".json");
            yield return (ResolveRoot(settings.EditModeBinaryRoot) ?? DefaultRoot("binary"), ".bin");
        }

        private static string DefaultRoot(string leaf)
            => Path.Combine(Application.persistentDataPath, "PersistenceKit", leaf);

        private static DiskTargetBase MakeDiskTarget(string configuredRoot, bool json)
        {
            var root = ResolveRoot(configuredRoot);
            if (json) return root == null ? new JsonDiskTarget()   : new JsonDiskTarget(root);
            return          root == null ? new BinaryDiskTarget() : new BinaryDiskTarget(root);
        }

        /// <summary>Empty → null (target picks its own default). Relative → resolved against the project root.</summary>
        private static string ResolveRoot(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return null;
            var trimmed = configured.Trim();
            try { return Path.GetFullPath(trimmed); }   // cwd is the project root in the editor
            catch (Exception ex)
            {
                Debug.LogWarning($"[PersistenceKit] Edit-mode root '{trimmed}' is not a usable path ({ex.Message}); falling back to the default.");
                return null;
            }
        }

        private static IEncryptor TryMakeEncryptor(PersistenceKitSettings settings, out string error)
        {
            error = null;
            var raw = settings.EditModeEncryptionKey;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            byte[] key;
            try { key = Convert.FromBase64String(raw.Trim()); }
            catch
            {
                error = "Edit-mode encryption key isn't valid Base64 — [Encrypted] fields won't open.";
                return null;
            }
            if (key.Length != 32)
            {
                error = $"Edit-mode encryption key must decode to 32 bytes, got {key.Length} — [Encrypted] fields won't open.";
                return null;
            }
            return new AesGcmEncryptor(new ConstantKeyProvider(key));
        }

        // ─── Mask backup / restore ───────────────────────────────

        private static void BackupMasks()
        {
            _maskBackup.Clear();
            foreach (var rs in PersistentStateRegistry.Snapshot())
            {
                var f = MaskField(rs.Type);
                if (f == null) continue;
                try { _maskBackup[f] = f.GetValue(null); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PersistenceKit] Couldn't snapshot {rs.Type.Name}.{MaskFieldName}: {ex.Message}");
                }
            }
        }

        private static void RestoreMasks()
        {
            foreach (var kv in _maskBackup)
            {
                try { kv.Key.SetValue(null, kv.Value); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PersistenceKit] Couldn't restore {kv.Key.DeclaringType?.Name}.{MaskFieldName}: {ex.Message}");
                }
            }
            _maskBackup.Clear();
        }

        /// <summary>
        /// The generated <c>private static PersistTargetMask __mask</c> on a state's partial.
        /// Absent on hand-written test fixtures; a rename in a future generator makes this
        /// null, which costs us the restore but nothing else.
        /// </summary>
        private static FieldInfo MaskField(Type stateType)
        {
            var f = stateType.GetField(MaskFieldName, BindingFlags.Static | BindingFlags.NonPublic);
            return f != null && f.FieldType == typeof(PersistTargetMask) ? f : null;
        }
    }
}
