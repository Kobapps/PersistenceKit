using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace PersistenceKit.Autosave
{
    /// <summary>
    /// Optional MonoBehaviour that watches a <see cref="PersistenceManager"/> for dirty
    /// states and flushes them. Mutations within a debounce window collapse into a single
    /// <see cref="PersistenceManager.SaveAllAsync"/>; <c>OnApplicationPause(true)</c> and
    /// <c>OnApplicationQuit</c> force-flush regardless of the debounce.
    /// </summary>
    /// <remarks>
    /// The loop is opt-in: drop it on a GameObject and call <see cref="Bind"/> with your
    /// manager, or instantiate from code via <see cref="Install"/>. Without an
    /// <c>AutoSaveLoop</c>, callers are expected to call <see cref="PersistenceManager.SaveAsync"/>
    /// or <see cref="PersistenceManager.SaveAllAsync"/> explicitly.
    /// </remarks>
    [DefaultExecutionOrder(short.MaxValue)]
    public sealed class AutoSaveLoop : MonoBehaviour
    {
        /// <summary>Coarse status reflecting whether sync is happening, paused, or off.</summary>
        public enum SyncStatus
        {
            /// <summary>No loop bound to a manager (or no loop exists at all).</summary>
            Disabled = 0,
            /// <summary>Loop bound, enabled, not paused — dirty events flush after the debounce.</summary>
            Active   = 1,
            /// <summary>Loop bound but suspended via <see cref="Pause"/> — dirty events accumulate but don't flush.</summary>
            Paused   = 2,
            /// <summary>Loop bound but the component is disabled (<see cref="Stop"/>) — Update tick doesn't run.</summary>
            Stopped  = 3,
        }

        // Process-wide registry of every AutoSaveLoop. Editor uses this for the sync widget;
        // runtime callers can use it for one-off shutdown drains.
        private static readonly List<AutoSaveLoop> _activeLoops = new List<AutoSaveLoop>();
        private static readonly object _activeLock = new object();

        public static List<AutoSaveLoop> ActiveLoops
        {
            get { lock (_activeLock) return new List<AutoSaveLoop>(_activeLoops); }
        }

        [Tooltip("Time in seconds to coalesce burst mutations into one Save. Mutations during the window slide the deadline.")]
        [SerializeField] private float _debounceSeconds = 0.5f;

        private PersistenceManager _kit;
        private bool  _pendingFlush;
        private float _flushAtTime;
        private bool  _isFlushing;
        private bool  _isPaused;

        // Set by OnDirty from whatever thread marked the state; consumed on the main thread in
        // Update. volatile so the Update tick is guaranteed to observe a threadpool write.
        private volatile bool _dirtySignal;

        /// <summary>Current sync status for this loop.</summary>
        public SyncStatus Status =>
            _kit == null ? SyncStatus.Disabled :
            !enabled    ? SyncStatus.Stopped  :
            _isPaused   ? SyncStatus.Paused   :
                          SyncStatus.Active;

        /// <summary>Bind this loop to a <see cref="PersistenceManager"/> at runtime.</summary>
        /// <remarks>
        /// Arms immediately if the manager is already dirty. Writes made before the loop existed
        /// raised their <c>OnDirty</c> edge with nobody subscribed, and once a bit is set no later
        /// write re-raises it — so the usual bootstrap (build, load, mutate, then
        /// <see cref="Install"/>) would otherwise never flush those first mutations.
        /// </remarks>
        public void Bind(PersistenceManager kit)
        {
            UnbindIfNeeded();
            _kit = kit;
            if (_kit == null) return;
            _kit.Dirty.OnDirty += OnDirty;
            ArmIfDirty();
        }

        /// <summary>Convenience: create a hidden GameObject hosting the loop bound to <paramref name="kit"/>.</summary>
        public static AutoSaveLoop Install(PersistenceManager kit, float debounceSeconds = 0.5f)
        {
            var go = new GameObject("[PersistenceKit.AutoSaveLoop]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            var loop = go.AddComponent<AutoSaveLoop>();
            loop._debounceSeconds = debounceSeconds;
            loop.Bind(kit);
            return loop;
        }

        // ─── Pause / Stop / Resume / Start (per-instance and aggregate) ───

        /// <summary>Suspend dirty-event handling. Mutations still accumulate dirty bits but no flush fires.</summary>
        public void Pause()
        {
            _isPaused = true;
            _pendingFlush = false;
            _dirtySignal  = false;   // don't let a signal raced in before the pause re-arm us
        }

        /// <summary>Resume dirty-event handling, re-arming the debounce if anything is already dirty.</summary>
        /// <remarks>
        /// The re-arm is load-bearing, not a nicety: mutations made while paused set their dirty
        /// bits without raising an edge anyone was listening to. See <see cref="ArmIfDirty"/>.
        /// </remarks>
        public void Resume()
        {
            _isPaused = false;
            ArmIfDirty();
        }

        /// <summary>
        /// Disable the component. The Update tick stops; pending dirty state is best-effort
        /// flushed via <see cref="OnDisable"/>. The kit binding is preserved so
        /// <see cref="StartLoop"/> can resume cleanly without re-binding.
        /// </summary>
        public void Stop() => enabled = false;

        /// <summary>Re-enable the component after a <see cref="Stop"/>.</summary>
        public void StartLoop() => enabled = true;

        /// <summary>Aggregate status across every active <see cref="AutoSaveLoop"/> in the process.</summary>
        public static SyncStatus AggregateStatus()
        {
            var loops = ActiveLoops;
            if (loops.Count == 0) return SyncStatus.Disabled;
            bool anyStopped = false, anyPaused = false;
            for (int i = 0; i < loops.Count; i++)
            {
                switch (loops[i].Status)
                {
                    case SyncStatus.Stopped:
                    case SyncStatus.Disabled: anyStopped = true; break;
                    case SyncStatus.Paused:   anyPaused = true; break;
                }
            }
            if (anyStopped) return SyncStatus.Stopped;
            if (anyPaused)  return SyncStatus.Paused;
            return SyncStatus.Active;
        }

        public static void PauseAll()  { foreach (var l in ActiveLoops) l.Pause();    }
        public static void ResumeAll() { foreach (var l in ActiveLoops) l.Resume();   }
        public static void StopAll()   { foreach (var l in ActiveLoops) l.Stop();     }
        public static void StartAll()  { foreach (var l in ActiveLoops) l.StartLoop(); }

        // ─── Lifecycle ─────────────────────────────────────────

        private void Awake()
        {
            // Awake fires once when the component is created — runs even if enabled=false at
            // start, which we want so a Stopped loop still appears in the editor's sync widget.
            lock (_activeLock) if (!_activeLoops.Contains(this)) _activeLoops.Add(this);
        }

        /// <summary>Re-arm after a <see cref="Stop"/>: writes made while disabled raised no edge we acted on.</summary>
        private void OnEnable() => ArmIfDirty();

        private void OnDestroy()
        {
            UnbindIfNeeded();
            lock (_activeLock) _activeLoops.Remove(this);
        }

        /// <summary>Raise a thread-safe flag only. The debounce is armed on the main thread in <see cref="Update"/>.</summary>
        /// <remarks>
        /// This runs on whatever thread called <see cref="DirtyTracker.Mark"/>, so it must not
        /// touch the Unity API. <c>PersistenceManager.SaveAsync</c> awaits its targets with
        /// <c>ConfigureAwait(false)</c>, so its failure path — the <c>finally</c> that re-marks
        /// every target it could not confirm as written — runs on a threadpool thread. Reading
        /// <c>Time.unscaledTime</c> here threw <c>UnityException</c> out of <c>Mark</c> and
        /// aborted that loop partway, discarding the dirty bits of every target after the first:
        /// the mechanism that exists to preserve writes across a failed save was destroying them,
        /// and the UnityException masked the original I/O error on the way out.
        /// </remarks>
        private void OnDirty(string _, PersistTarget __)
        {
            if (_isPaused) return;        // accumulate dirty bits but don't schedule a flush
            _dirtySignal = true;
        }

        private void Update()
        {
            if (_isPaused || _kit == null) return;

            // Consume the cross-thread signal here, where the Unity clock is legal to read.
            if (_dirtySignal)
            {
                _dirtySignal = false;
                _pendingFlush = true;
                _flushAtTime  = Time.unscaledTime + Mathf.Max(0f, _debounceSeconds);
            }

            if (!_pendingFlush) return;
            if (Time.unscaledTime < _flushAtTime) return;
            FlushFireAndForget();
        }

        /// <summary>
        /// Schedule a flush when the manager holds unsaved writes, regardless of whether an
        /// <c>OnDirty</c> edge was ever observed.
        /// </summary>
        /// <remarks>
        /// <see cref="DirtyTracker"/>'s event is an edge, not a level: <c>Mark</c> raises nothing
        /// when the bit is already set. So any window where this loop ignored or missed an edge —
        /// paused, unbound, disabled, or mid-flush — leaves writes that no future <c>Mark</c> will
        /// ever announce. Re-reading the tracker is the only way to pick them back up.
        /// </remarks>
        private void ArmIfDirty()
        {
            if (_kit == null || _isPaused || !_kit.Dirty.HasDirty) return;
            _pendingFlush = true;
            _flushAtTime  = Time.unscaledTime + Mathf.Max(0f, _debounceSeconds);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) FlushFireAndForget();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// WebGL never gets a dependable <c>OnApplicationQuit</c> — a closing tab simply stops
        /// executing, and there is no way to run a save from <c>beforeunload</c>. Losing focus
        /// is the last callback we can count on, so treat it as a save point: it fires when the
        /// player switches tab or clicks away, which is what precedes almost every close.
        /// The flush is cheap when nothing is dirty (SaveAllAsync only writes dirty targets).
        /// </summary>
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) FlushFireAndForget();
        }
#endif

        private void OnApplicationQuit() => FlushFireAndForget();

        private void OnDisable()
        {
            // Drain any pending dirty state before going inactive. We do NOT unbind here so
            // that Stop()/StartLoop() can round-trip cleanly. Unbinding happens in OnDestroy.
            FlushFireAndForget();
        }

        private async void FlushFireAndForget()
        {
            if (_isFlushing || _kit == null) return;
            _isFlushing  = true;
            _pendingFlush = false;
            try
            {
                await _kit.SaveAllAsync(CancellationToken.None);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                _isFlushing = false;
                // A flush that threw partway leaves states still dirty while _pendingFlush is
                // already false — and their bits are set, so no later write re-raises OnDirty.
                // Re-arm from the tracker so the next tick retries instead of stranding them
                // until an unrelated key happens to go dirty.
                // Safe on the main thread: the await above has no ConfigureAwait(false), so this
                // continuation resumes on Unity's SynchronizationContext.
                ArmIfDirty();
            }
        }

        private void UnbindIfNeeded()
        {
            if (_kit == null) return;
            _kit.Dirty.OnDirty -= OnDirty;
            _kit = null;
        }
    }
}
