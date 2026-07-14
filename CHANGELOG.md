# Changelog

All notable changes to PersistenceKit are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Edit-mode state inspection.** The PersistenceKit window now reads, edits and resets saved
  states without entering Play mode. It builds an editor-owned manager against your save files
  from Project Settings → PersistenceKit → Edit Mode (default target, storage roots, and an
  optional AES key for `[Encrypted]` states). Every tab works against it unchanged.
- **WebGL save durability.** Writes are now committed to IndexedDB via a bundled `.jslib`
  (`FS.syncfs`, coalesced) after every disk save, disk delete and PlayerPrefs write. Previously
  saves lived only in the in-memory IDBFS mirror and vanished on reload. `AutoSaveLoop` also
  flushes on focus loss on WebGL, where `OnApplicationQuit` never fires on tab close.
- Inspector: filter box, per-target dirty dots, fields grouped by target,
  changed-from-default highlighting, and a summary strip.

### Fixed
- **Autosave silently stopped saving after a pause.** `DirtyTracker` raises `OnDirty` only when
  a write *adds* a bit, so mutations made while paused scheduled no flush — and after resume,
  further writes to those keys raised nothing either, because their bits were already set.
  Affected writes were stranded until an unrelated key went dirty. `Resume`, `Bind` and
  re-enable now re-arm from the tracker, and a flush that fails partway re-arms instead of
  stranding the states it never reached.
- **A failed save could discard other targets' pending writes.** `SaveAsync` re-marks
  unconfirmed targets from a threadpool continuation; `AutoSaveLoop.OnDirty` read
  `Time.unscaledTime` there, throwing `UnityException` out of `Mark` and aborting the restore
  loop partway — losing every subsequent target's dirty bit and masking the original I/O error.
  The dirty handler is now thread-agnostic.
- **Reset All / JSON import / snapshot restore aborted on unwired targets.** They called
  `MarkDirty()`, which sets a state's whole mask, and `SaveAsync` throws on a target with no
  store behind it — sinking the write for the wired targets too. They now mark only wired targets.
- A failed snapshot restore left the Activity tab silently dropping every save event for the
  rest of the session.
- `NoOpEncryptor` pointed at `PersistenceKitBuilder.UseEncryption(...)`, which has never
  existed; the method is `UseEncryptor`.

### Documentation
- README: corrected the install URL (the package lives at the repo root, not under
  `Assets/PersistenceKit`), corrected the encryption section (the shipped cipher is
  AES-256-CBC + HMAC-SHA256 Encrypt-then-MAC, not AES-GCM — the class name is retained for ABI
  stability), and replaced the manual `FS.syncfs` WebGL caveat with the built-in behaviour.

## [0.1.0] - 2026-05-11

Initial release.

### Runtime
- Source-generated state classes via `[PersistentState]` + `[Persist]` / `[Encrypted]`
  attributes. Two field conventions supported: direct public PascalCase fields with
  manual `MarkDirty()`, and backing-field `_camelCase` that yields a generator-emitted
  property with auto-dirty in the setter.
- Per-field target routing across four built-in targets: `JsonDiskTarget`,
  `BinaryDiskTarget`, `PlayerPrefsTarget`, `RemoteTarget`.
- AES-256-CBC + HMAC-SHA256 encryption (`AesGcmEncryptor`) for `[Encrypted]` leaves —
  portable across Unity's Mono runtime (no `System.Security.Cryptography.AesGcm`
  dependency).
- Pluggable serializers via `ISerializerHandler`. Ships Newtonsoft.Json (default) and
  System.Text.Json handlers gated by version-defines.
- `PersistenceKitBuilder` fluent configuration; `IRemotePersistenceProvider` interface
  for user-supplied cloud backends; in-memory provider for tests.
- `AutoSaveLoop` MonoBehaviour with debounced flush, pause / resume / stop / start,
  application-pause and quit force-flush, static registry exposed to the editor.
- Profiler markers on hot paths: `PersistenceKit.Save` / `Load` / `SaveAll` / `Delete`,
  `Serialize` / `Deserialize`, `Encrypt` / `Decrypt`, `DiskTarget.Read` / `Write`.

### Editor
- `Window → PersistenceKit → Inspector` window with four tabs: Inspector, Storage,
  Snapshots, Activity. Kobapps design system; live refresh interval configurable.
- Inspector tab: state tree with dirty pills, editable field grid with Odin /
  SerializeReference / reflection drawer fallbacks (auto-detected), per-state Save /
  Mark Dirty / Delete actions, lock-icon for encrypted fields.
- Storage tab: browse on-disk JSON / Binary files, PlayerPrefs entries, Remote keys
  for cached states. Reveal Folder + per-row Reveal File buttons. UTF-8 preview pane.
- Snapshots tab: whole-world capture under a label, restore later. One JSON file per
  snapshot. Configurable folder (Project Settings → PersistenceKit). Reveal folder and
  per-snapshot reveal in OS file manager.
- Activity tab: reverse-chronological save / export / import / snapshot-capture /
  snapshot-restore / reset log with per-field change diffs and ↶ Undo on save rows.
- Sync widget in the tab row: colour-coded status dot (gray=disabled, green=active,
  yellow=paused, red=stopped) + Pause/Resume + Stop/Start.
- Export / Import the whole world to a flat JSON dictionary keyed by storage key.
- Reset All button: wipes every cached state's persisted fields to defaults and saves
  through every wired target, preserving runtime references.
- Project Settings page (`Edit → Project Settings → PersistenceKit`): field renderer
  choice (Auto / Odin / Default), refresh interval, chip visibility, log capacity,
  default debounce, snapshots folder.

### Generator
- Roslyn 4.3.1-compatible source generator (`PersistenceKit.SourceGenerator.dll`,
  loaded via `RoslynAnalyzer` label). Diagnostics PK001–PK007.
- Emits per-field target slots, payload writers / readers, dirty hooks, public
  `MarkDirty()` / `MarkDirty(target)` methods, registry registration via
  `[InitializeOnLoadMethod]` + `[RuntimeInitializeOnLoadMethod]`. Idempotent — Play
  mode double-register is a silent no-op.

### Tests
- EditMode suite: core, slots, isolation, targets, encryption, default-target,
  end-to-end, Newtonsoft handler.
- PlayMode suite: `AutoSaveLoop` integration (debounce, pause/resume, stop/start,
  Install lifecycle, shutdown drain).

### DI
- Optional `PersistenceKit.DI.VContainer` and `PersistenceKit.DI.Zenject` asmdefs gated
  by version-defines. Activate automatically when the corresponding container package
  is installed.
