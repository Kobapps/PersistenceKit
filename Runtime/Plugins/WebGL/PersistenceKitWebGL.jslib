// Flushes the emscripten in-memory filesystem to IndexedDB.
//
// On WebGL, Application.persistentDataPath is an IDBFS mount that lives in RAM. Writes only
// reach IndexedDB — the thing that actually survives a page reload — when FS.syncfs runs.
// Without this, a save looks fine for the rest of the session and is gone on the next visit.
//
// syncfs is asynchronous and must not be re-entered: overlapping calls can interleave and
// drop writes. So we run at most one at a time and coalesce anything requested meanwhile
// into a single trailing run, which also keeps a burst of saves from queuing a burst of
// IndexedDB transactions.
var PersistenceKitWebGLLib = {
  $PersistenceKitFS: {
    syncing: false,
    pending: false,
    run: function () {
      if (PersistenceKitFS.syncing) {
        PersistenceKitFS.pending = true;
        return;
      }
      PersistenceKitFS.syncing = true;
      try {
        FS.syncfs(false, function (err) {
          PersistenceKitFS.syncing = false;
          if (err) {
            console.error('[PersistenceKit] IndexedDB sync failed: ' + err);
          }
          if (PersistenceKitFS.pending) {
            PersistenceKitFS.pending = false;
            PersistenceKitFS.run();
          }
        });
      } catch (e) {
        // Never let a filesystem hiccup take the player loop down with it.
        PersistenceKitFS.syncing = false;
        console.error('[PersistenceKit] IndexedDB sync threw: ' + e);
      }
    }
  },

  PersistenceKit_FlushStorage: function () {
    PersistenceKitFS.run();
  }
};

autoAddDeps(PersistenceKitWebGLLib, '$PersistenceKitFS');
mergeInto(LibraryManager.library, PersistenceKitWebGLLib);
