using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;

namespace PersistenceKit.Targets
{
    /// <summary>
    /// Shared atomic-file plumbing for the JSON and binary disk targets. Writes to
    /// <c>&lt;file&gt;.tmp</c> first then renames over the existing file (using
    /// <see cref="File.Replace"/> when present) so a crash mid-write leaves the previous
    /// good file untouched.
    /// </summary>
    public abstract class DiskTargetBase : IPersistenceTarget
    {
        private static readonly ProfilerMarker _markerWrite = new ProfilerMarker("PersistenceKit.DiskTarget.Write");
        private static readonly ProfilerMarker _markerRead  = new ProfilerMarker("PersistenceKit.DiskTarget.Read");

        private readonly string _rootDir;
        private readonly string _extension;
        private readonly Dictionary<string, SemaphoreSlim> _keyLocks = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private readonly object _keyLocksGate = new object();

        protected DiskTargetBase(string rootDir, string extension)
        {
            if (string.IsNullOrEmpty(rootDir)) throw new ArgumentException("rootDir required", nameof(rootDir));
            if (string.IsNullOrEmpty(extension) || !extension.StartsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("extension must start with '.'", nameof(extension));
            _rootDir = rootDir;
            _extension = extension;
            Directory.CreateDirectory(_rootDir);
        }

        public abstract PersistTarget Target { get; }

        public string RootDirectory => _rootDir;

        // WebGL is single-threaded with a synchronous emscripten filesystem: there is no
        // overlapped I/O to overlap with, and asking for it only adds machinery.
#if UNITY_WEBGL && !UNITY_EDITOR
        private const bool UseAsyncIO = false;
#else
        private const bool UseAsyncIO = true;
#endif

        public async ValueTask SaveAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            using var __pm = _markerWrite.Auto();
            var path = PathFor(key);
            var tmp  = path + ".tmp";
            var sem  = LockFor(key);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: UseAsyncIO))
                {
                    var seg = payload.ToArray();
                    await fs.WriteAsync(seg, 0, seg.Length, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);

                    // Durability: FlushAsync only pushes the managed buffer to the OS — the
                    // bytes can still be sitting in the OS page cache when the atomic rename
                    // below records the new file in the directory. On a power-cut that ordering
                    // yields a zero-length/truncated save (a hard failure on console cert's
                    // power-loss tests). Flush(true) issues FlushFileBuffers/fsync so the data
                    // is on the device before we swap it in. Guarded: a handful of platforms /
                    // virtual filesystems report it unsupported, where we accept OS-cache durability.
#if !UNITY_WEBGL || UNITY_EDITOR
                    try { fs.Flush(flushToDisk: true); }
                    catch (Exception) { /* best effort — platform without a real fsync */ }
#endif
                }

                // Rename onto the live file. File.Replace gives us a true atomic swap on
                // platforms that support it (Windows / macOS / Linux). WebGL's IDBFS-backed
                // virtual filesystem doesn't implement Replace reliably and can hang the
                // page; fall back to Delete + Move there.
#if UNITY_WEBGL && !UNITY_EDITOR
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                // The file now exists in IDBFS's in-memory image only. Push it to IndexedDB or
                // the save evaporates when the player closes the tab.
                WebGLStorage.RequestFlush();
#else
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmp, path);
                }
#endif
            }
            finally
            {
                sem.Release();
            }
        }

        public async ValueTask<byte[]> LoadAsync(string key, CancellationToken ct)
        {
            using var __pm = _markerRead.Auto();
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var sem = LockFor(key);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
                var len = (int)fs.Length;
                var buf = new byte[len];
                int read = 0;
                while (read < len)
                {
                    var n = await fs.ReadAsync(buf, read, len - read, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                return buf;
            }
            finally
            {
                sem.Release();
            }
        }

        public async ValueTask DeleteAsync(string key, CancellationToken ct)
        {
            // Take the same per-key lock Save/Load use. Without it a delete could race an
            // in-flight save on the same key — deleting the live file or its .tmp mid-write and
            // making the subsequent File.Replace/File.Move throw or resurrect partial data.
            var path = PathFor(key);
            var tmp  = path + ".tmp";
            var sem  = LockFor(key);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (FileNotFoundException) { }
                try { if (File.Exists(tmp))  File.Delete(tmp);  } catch (FileNotFoundException) { }

#if UNITY_WEBGL && !UNITY_EDITOR
                // A delete is a write too — unflushed, the file is still in IndexedDB and
                // reappears on the next load.
                WebGLStorage.RequestFlush();
#endif
            }
            finally
            {
                sem.Release();
            }
        }

        public bool Exists(string key) => File.Exists(PathFor(key));

        protected string PathFor(string key)
        {
            var safe = SanitizeKey(key);
            return Path.Combine(_rootDir, safe + _extension);
        }

        private SemaphoreSlim LockFor(string key)
        {
            lock (_keyLocksGate)
            {
                if (!_keyLocks.TryGetValue(key, out var sem))
                {
                    sem = new SemaphoreSlim(1, 1);
                    _keyLocks[key] = sem;
                }
                return sem;
            }
        }

        // Replace path-illegal characters in keys (slot ids may contain ':' etc) so the file
        // name is portable across operating systems.
        private static string SanitizeKey(string key)
        {
            // Replace ':' (slot separator) with '_' and any other invalid chars likewise.
            var invalid = Path.GetInvalidFileNameChars();
            var span = key.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                var c = span[i];
                if (c == ':' || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                    return SanitizeSlow(key, invalid);
                for (int j = 0; j < invalid.Length; j++)
                    if (c == invalid[j]) return SanitizeSlow(key, invalid);
            }
            return key;
        }

        private static string SanitizeSlow(string key, char[] invalid)
        {
            var chars = key.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c == ':' || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) { chars[i] = '_'; continue; }
                for (int j = 0; j < invalid.Length; j++)
                    if (c == invalid[j]) { chars[i] = '_'; break; }
            }
            return new string(chars);
        }
    }
}
