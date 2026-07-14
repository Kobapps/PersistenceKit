using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
#endif

namespace PersistenceKit.Targets
{
    /// <summary>
    /// Commits WebGL writes to IndexedDB. A no-op on every other platform, where the OS
    /// filesystem is already the durable store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WebGL mounts <see cref="Application.persistentDataPath"/> as an IDBFS filesystem that
    /// is held in memory. A <c>File.Write</c> there updates RAM only; the bytes reach
    /// IndexedDB — and therefore survive a reload — when emscripten's <c>FS.syncfs</c> runs.
    /// A save that skips it reads back correctly for the rest of the session and is silently
    /// gone the next time the player opens the page, which is the worst shape a save bug can
    /// take.
    /// </para>
    /// <para>
    /// The flush is fire-and-forget by nature: <c>FS.syncfs</c> is asynchronous in JS and
    /// there is no way to block a single-threaded page on it. Requests issued while one is
    /// in flight coalesce into a single trailing run (see PersistenceKitWebGL.jslib), so
    /// calling this after every write is cheap.
    /// </para>
    /// </remarks>
    public static class WebGLStorage
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void PersistenceKit_FlushStorage();

        // The jslib ships with the package, but a project can exclude it from the build (or
        // strip it) — in which case the extern is missing and every write would otherwise
        // throw. Warn once, then stay quiet.
        private static bool _unavailable;
#endif

        /// <summary>
        /// Ask the browser to persist everything written so far. Returns immediately; the
        /// write completes on the JS event loop. No-op off WebGL.
        /// </summary>
        public static void RequestFlush()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_unavailable) return;
            try
            {
                PersistenceKit_FlushStorage();
            }
            catch (EntryPointNotFoundException)
            {
                _unavailable = true;
                Debug.LogWarning(
                    "[PersistenceKit] PersistenceKitWebGL.jslib is missing from this build, so saves are not being " +
                    "committed to IndexedDB and will be lost on reload. Make sure " +
                    "Runtime/Plugins/WebGL/PersistenceKitWebGL.jslib is included for the WebGL platform.");
            }
            catch (DllNotFoundException)
            {
                _unavailable = true;
                Debug.LogWarning(
                    "[PersistenceKit] Could not reach the WebGL storage flush; saves may not survive a reload.");
            }
#endif
        }
    }
}
