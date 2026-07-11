# Changelog

All notable changes to PersistenceKit are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
