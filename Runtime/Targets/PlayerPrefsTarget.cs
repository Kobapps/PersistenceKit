using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PersistenceKit.Targets
{
    /// <summary>
    /// Persists payloads in <see cref="PlayerPrefs"/> as base64 strings. Suitable for small
    /// values (settings, counters); warns above the soft limit (default 1 MB) and refuses
    /// above the hard limit (default 2 MB) since PlayerPrefs is a poor fit for large blobs.
    /// </summary>
    /// <remarks>
    /// <see cref="PlayerPrefs"/> can only be called from Unity's main thread. The manager's
    /// async pipeline uses <c>ConfigureAwait(false)</c>, so continuations after the first
    /// truly-async target (disk I/O, remote PUT) land on the threadpool. To keep PlayerPrefs
    /// safe regardless of the calling thread, this target captures Unity's main-thread
    /// <see cref="SynchronizationContext"/> at construction and marshals every operation back
    /// to it when it isn't already there.
    /// <para>
    /// WebGL note: the platform is single-threaded and PlayerPrefs is already synchronous on
    /// the main thread. The dispatch path can stall on WebGL because a <c>ConfigureAwait(false)</c>
    /// continuation nulls <see cref="SynchronizationContext.Current"/>, falling into the Post +
    /// blocking-wait branch with no thread free to drain the queue. We bypass the dispatch
    /// on WebGL entirely.
    /// </para>
    /// </remarks>
    public sealed class PlayerPrefsTarget : IPersistenceTarget
    {
        private const string KeyPrefix = "pk:";
        private readonly long _softLimitBytes;
        private readonly long _hardLimitBytes;
        private readonly SynchronizationContext _mainContext;

        public PlayerPrefsTarget(long softLimitBytes = 1L * 1024 * 1024, long hardLimitBytes = 2L * 1024 * 1024)
        {
            _softLimitBytes = softLimitBytes;
            _hardLimitBytes = hardLimitBytes;
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL has no threadpool; we never dispatch. The captured context is unused.
            _mainContext = SynchronizationContext.Current;
#else
            _mainContext = SynchronizationContext.Current
                           ?? throw new InvalidOperationException(
                               "PlayerPrefsTarget must be constructed on the Unity main thread (no SynchronizationContext was captured).");
#endif
        }

        public PersistTarget Target => PersistTarget.PlayerPrefs;

        public ValueTask SaveAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            // Validate up-front — the work is identical on either thread.
            if (payload.Length > _hardLimitBytes)
                throw new InvalidOperationException(
                    $"PlayerPrefsTarget: payload for '{key}' is {payload.Length} bytes, exceeds hard limit {_hardLimitBytes}. " +
                    "PlayerPrefs is not appropriate for blobs this large — route this state to JSON or Binary.");
            if (payload.Length > _softLimitBytes)
                Debug.LogWarning($"PlayerPrefsTarget: payload for '{key}' is {payload.Length} bytes; consider routing to JSON or Binary.");

            // Materialize bytes before the dispatch so we don't capture a Span/Memory across threads.
            var b64 = Convert.ToBase64String(payload.Span);
#if UNITY_WEBGL && !UNITY_EDITOR
            PlayerPrefs.SetString(KeyPrefix + key, b64);
            PlayerPrefs.Save();
            return default;
#else
            return RunOnMain(() =>
            {
                PlayerPrefs.SetString(KeyPrefix + key, b64);
                PlayerPrefs.Save();
            });
#endif
        }

        public ValueTask<byte[]> LoadAsync(string key, CancellationToken ct)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var ppKey = KeyPrefix + key;
            if (!PlayerPrefs.HasKey(ppKey)) return new ValueTask<byte[]>((byte[])null);
            var b64 = PlayerPrefs.GetString(ppKey, null);
            if (string.IsNullOrEmpty(b64)) return new ValueTask<byte[]>(Array.Empty<byte>());
            return new ValueTask<byte[]>(Convert.FromBase64String(b64));
#else
            return RunOnMain(() =>
            {
                var ppKey = KeyPrefix + key;
                if (!PlayerPrefs.HasKey(ppKey)) return (byte[])null;
                var b64 = PlayerPrefs.GetString(ppKey, null);
                if (string.IsNullOrEmpty(b64)) return Array.Empty<byte>();
                return Convert.FromBase64String(b64);
            });
#endif
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var ppKey = KeyPrefix + key;
            if (PlayerPrefs.HasKey(ppKey))
            {
                PlayerPrefs.DeleteKey(ppKey);
                PlayerPrefs.Save();
            }
            return default;
#else
            return RunOnMain(() =>
            {
                var ppKey = KeyPrefix + key;
                if (PlayerPrefs.HasKey(ppKey))
                {
                    PlayerPrefs.DeleteKey(ppKey);
                    PlayerPrefs.Save();
                }
            });
#endif
        }

        public bool Exists(string key)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // Single-threaded — call PlayerPrefs directly. No dispatch, no GetAwaiter().GetResult()
            // which would deadlock the page (the only thread can't drain the Post queue).
            return PlayerPrefs.HasKey(KeyPrefix + key);
#else
            // Sync API — must block to honour the contract. Use the same dispatch helper so
            // a background-thread caller still gets a correct answer.
            if (SynchronizationContext.Current == _mainContext)
                return PlayerPrefs.HasKey(KeyPrefix + key);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainContext.Post(_ =>
            {
                try { tcs.SetResult(PlayerPrefs.HasKey(KeyPrefix + key)); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task.GetAwaiter().GetResult();
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        // ─── Main-thread dispatch helpers (skipped on WebGL — single-threaded) ───

        private ValueTask RunOnMain(Action work)
        {
            if (SynchronizationContext.Current == _mainContext)
            {
                work();
                return default;
            }
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainContext.Post(_ =>
            {
                try { work(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return new ValueTask(tcs.Task);
        }

        private ValueTask<T> RunOnMain<T>(Func<T> work)
        {
            if (SynchronizationContext.Current == _mainContext)
                return new ValueTask<T>(work());

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainContext.Post(_ =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return new ValueTask<T>(tcs.Task);
        }
#endif
    }
}
