# PersistenceKit

A persistent save system for Unity 6.

- **Field-level opt-in** via `[Persist]` — no surprise serialization of unrelated fields.
- **Per-field target routing** — same state class can put a flag in PlayerPrefs, an inventory in cloud, and a name on disk.
- **Source-generated boilerplate** — no hand-written setters, no state-map asset.
- **Pluggable serializers** — Newtonsoft.Json ships by default; System.Text.Json optional; bring your own with one interface.
- **Per-field AES-256 encryption** — opt in with `[Encrypted]`.
- **Standalone or DI** — works without a container; thin VContainer + Zenject adapters available.
- **Multiple instances of the same type** via slot ids.
- **Full editor inspector** — live state grid with Odin / SerializeReference fallbacks, dirty pills, undo, export / import, snapshots.

## Installation

PersistenceKit ships as a UPM package. Three ways to install:

**1. Git URL** (`Packages/manifest.json` → add to `dependencies`):

```json
"com.kobapps.persistencekit": "https://github.com/Kobapps/PersistenceKit.git"
```

**2. Package Manager → Add → Add package from git URL** → paste the URL above.

**3. Local folder** (during development): `Package Manager → Add → Add package from disk` → pick the folder containing `package.json`.

Requires Unity **6000.0** or newer. Newtonsoft.Json (`com.unity.nuget.newtonsoft-json` ≥ 3.0.0) is pulled in automatically as a transitive dependency.

### WebGL notes

The kit runs on WebGL with two platform-aware behaviours baked in:

- `PlayerPrefsTarget` bypasses its `SynchronizationContext` dispatch on WebGL.
  WebGL is single-threaded; a `ConfigureAwait(false)` continuation can null out
  `SynchronizationContext.Current`, and the fallback Post + blocking-wait would
  deadlock the only thread. The WebGL path calls `PlayerPrefs` directly.
- Disk targets fall back to `File.Delete` + `File.Move` on WebGL. Unity's IDBFS-backed
  filesystem doesn't implement `File.Replace` reliably; the swap is therefore
  non-atomic on WebGL (everywhere else it is atomic via `File.Replace`).

WebGL durability is handled for you. `Application.persistentDataPath` on WebGL is an IDBFS
mount that lives in memory, so a write only reaches IndexedDB — the thing that survives a
reload — once emscripten's `FS.syncfs` runs. The kit ships
`Runtime/Plugins/WebGL/PersistenceKitWebGL.jslib` and calls it after every disk save, disk
delete, and PlayerPrefs write, coalescing concurrent syncs. Without that, a save reads back
fine for the rest of the session and is silently gone the next time the player opens the page.

You can force a flush yourself with `PersistenceKit.Targets.WebGLStorage.RequestFlush()`
(a no-op off WebGL). If the `.jslib` is ever excluded from your build, the kit logs a warning
once and saves stop surviving reloads — so keep it in.

Remaining WebGL notes:

- `AutoSaveLoop` also flushes on focus loss on WebGL: a closing tab never fires
  `OnApplicationQuit` and nothing can save from `beforeunload`, so losing focus is the last
  callback available.
- The `RemoteTarget` works fine on WebGL but any `IRemotePersistenceProvider` you
  supply must use `UnityWebRequest` (not `HttpClient`, which doesn't work on WebGL).

After install, the **Rich Save Sample** is importable from the Package Manager → PersistenceKit → Samples panel. It drops a scene-builder menu (`Tools → PersistenceKit → Configure Sample Scene`) and the runtime driver into `Assets/Samples/PersistenceKit/<version>/`.

## Quick start

Two conventions are supported for declaring persisted fields. **Direct fields** (recommended for new projects) — the field name is the public access path and you call `MarkDirty()` after a batch of mutations:

```csharp
[PersistentState]
public partial class PlayerState
{
    [Persist]                                    public string UserId;
    [Persist(target: PersistTarget.PlayerPrefs)] public int    SessionCount;
    [Persist(target: PersistTarget.Remote)]      public string CloudTag;
    [Persist, Encrypted]                         public string AuthToken;

    public float RuntimeOnly;   // no [Persist] — ignored
}

var player = await kit.LoadOrCreateAsync<PlayerState>();

player.UserId       = "kobi";        // direct field write, no auto-dirty
player.SessionCount = 12;
player.AuthToken    = "ya29.…";

player.MarkDirty();                  // flush every target in the state's mask
// or:
player.MarkDirty(PersistTarget.PlayerPrefs);   // flush a single target

await kit.SaveAsync(player);
```

**Property convention** (legacy) — use a leading-underscore backing field; the generator emits a public PascalCase property whose setter auto-marks dirty:

```csharp
[PersistentState]
public partial class PlayerState
{
    [Persist] private string _userId;          // generator emits  public string UserId { get; set; }
    [Persist(target: PersistTarget.PlayerPrefs)] private int _sessionCount;
}

var player = await kit.LoadOrCreateAsync<PlayerState>();
player.UserId = "kobi";       // setter dirties Json target automatically
await kit.SaveAsync(player);
```

Wire the kit once at startup (same for either convention):

```csharp
var kit = PersistenceKitBuilder.Default()
    .UseDefaultTarget(PersistTarget.Json)
    .UseTarget(PersistTarget.Json,        new JsonDiskTarget())
    .UseTarget(PersistTarget.PlayerPrefs, new PlayerPrefsTarget())
    .UseTarget(PersistTarget.Remote,      new RemoteTarget(new InMemoryRemoteProvider()))
    .UseSerializer(PersistTarget.Json,        new NewtonsoftJsonHandler())
    .UseSerializer(PersistTarget.PlayerPrefs, new NewtonsoftJsonHandler())
    .UseSerializer(PersistTarget.Remote,      new NewtonsoftJsonHandler())
    .UseEncryptor(new AesGcmEncryptor(new ConstantKeyProvider(myKey32Bytes)))
    .Build();

// Multiple instances of the same type via slot ids:
var slotA = await kit.LoadOrCreateAsync<PlayerState>("a");
var slotB = await kit.LoadOrCreateAsync<PlayerState>("b");
```

## Building the source generator

The generator's source lives in `Assets/PersistenceKit/SourceGenerator~/` (the `~` suffix
hides it from Unity). Build it with:

```powershell
cd Assets/PersistenceKit/SourceGenerator~
./build.ps1
```

The build drops `PersistenceKit.SourceGenerator.dll` into
`Assets/PersistenceKit/Runtime/Plugins/` with a `.meta` already labelled
`RoslynAnalyzer`. Refresh Unity (Ctrl+R) — generated partials become visible to your
state classes immediately.

If you prefer not to build the generator, you can implement `IPersistentState` by hand;
see `Tests/Runtime/Fixtures/TestStates.cs` for a worked example. The generator-produced
shape and the hand-written one are interoperable.

## Project settings

`Edit → Project Settings → PersistenceKit` exposes editor-time defaults:

- **Field renderer** — `Auto` (Odin if installed, else built-in), `Odin` (force Odin), or
  `Default` (force the built-in renderer even when Odin is available).
- **Refresh interval (ms)** — how often the inspector window's tree / status / activity
  tab re-paint. Lower = snappier but more CPU.
- **Show target chips / dirty pills / field types** — toggles for the inspector tab UI.
- **Activity log capacity** — ring-buffer size backing the Activity tab.
- **Default auto-save debounce (s)** — seeded into `RichSaveSample.DebounceSeconds` when
  `Tools → PersistenceKit → Configure Sample Scene` writes the scene; also the default
  `AutoSaveLoop.Install(...)` debounce in your own code.
- **Snapshots → Folder** — where whole-world snapshot `.json` files are written. Use
  **Browse…** to pick a folder (stored project-relative when it sits inside the project),
  or **Reveal** to open the current one. Relative paths resolve to the project root;
  absolute paths are used as-is.
- **AI Assistant → Install Claude Skill** — copies the bundled `persistencekit` skill into
  this project's `.claude/skills/` so Claude Code / the Agent SDK gets accurate integration
  guidance (state declaration, builder wiring, save/load, encryption, and a Unity-MCP
  verification workflow). The skill source ships with the package; the button installs a
  project-local copy you can re-run to update.

Settings are stored in `ProjectSettings/PersistenceKitSettings.asset` (project-scoped,
versioned with your repo) and written every time you change a value — no apply button.

## Editor inspector

`Window → PersistenceKit → Inspector` opens the in-editor tool. Four tabs:

- **Inspector** — left tree shows live `PersistenceManager` instances with their cached
  `(Type, Slot)` entries. Pick one to see the persisted-field grid: name, target chip,
  encryption badge, and a per-type editor (string/bool/int/long/float/double/enum render
  inline; complex types fall back to a JSON-log button). The right sidebar holds Save Now,
  Mark Dirty, and Delete actions.
- **Storage** — browses what's currently on disk under `JsonDiskTarget` /
  `BinaryDiskTarget`, plus PlayerPrefs and Remote keys for cached states. Click an entry
  for a UTF-8 preview. Disk targets get *Reveal Folder* + per-row *Reveal File* buttons.
- **Snapshots** — stash *every loaded state* in one shot under a label, restore later.
  Each snapshot is the whole world (same shape an Export produces) — Restore writes
  every state back into its live instance and triggers a save. Stored as one `.json` per
  snapshot under a configurable folder (default `<ProjectRoot>/PersistenceKitSnapshots/`;
  set the path in *Project Settings → PersistenceKit → Snapshots*). Lives outside
  `Library/` so cache wipes between test runs don't blow them away. Per row: *Restore*,
  *↗* to reveal the file in your OS file manager, *✕* to delete; "Open Folder" button
  reveals the snapshot directory.
- **Activity** — reverse-chronological tail of save / export / import events with
  per-field change diffs and ↶ Undo. The buffer holds the last *N* events
  (configurable in Project Settings).

Encrypted fields are masked in the Inspector tab; values appear in the on-disk preview
already encrypted, never in plaintext.

### Profiler markers

Save/load/encrypt hot paths emit `Unity.Profiling.ProfilerMarker` samples so you can see
them on the Profiler timeline. Look under **Scripts** for:

- `PersistenceKit.Save` / `PersistenceKit.SaveAll` / `PersistenceKit.Load` / `PersistenceKit.Delete`
- `PersistenceKit.Serialize` / `PersistenceKit.Deserialize`
- `PersistenceKit.Encrypt` / `PersistenceKit.Decrypt`
- `PersistenceKit.DiskTarget.Read` / `PersistenceKit.DiskTarget.Write`

Time is wall-clock (the markers span the full async await), so a slow remote PUT or a
blocked disk write surfaces as a long sample without further instrumentation.

### Export / Import

The tab row's **Export** and **Import** buttons round-trip every loaded state through a
single JSON file. The wire format is a flat dictionary keyed by storage key — no
metadata, no wrapper:

```json
{
  "PlayerProfile:slot1": {
    "DisplayName": "kobi",
    "Level": 5,
    "AuthToken": "letmein",
    "Stats": { "kills": 12 },
    "Achievements": ["first_blood"]
  },
  "InventoryState": {
    "Coins": 100,
    "Items": [ … ]
  }
}
```

- **Export** writes the dictionary above. Encrypted fields are exported as **plaintext** —
  the file is a portable backup, not a sealed envelope. Diffable in Git, hand-editable.
- **Import** parses the file, finds each matching live state by storage key, restores
  every field via reflection (deserializing JSON tokens to each field's declared type),
  then triggers a save. Encryption is **re-applied automatically** because the kit's
  serializer reads each field's `[Encrypted]` attribute at save time — you don't need to
  re-encrypt anything by hand.

Limitation: import only writes to states that are *currently loaded* in a manager. To
restore a state that hasn't been loaded yet, call `LoadOrCreateAsync<T>(slot)` for it once
(e.g., via your runtime code) and re-import.

## Optional features

- **System.Text.Json** — add `PERSISTENCEKIT_STJ` to Project Settings → Player →
  Scripting Define Symbols. The Newtonsoft and STJ handlers can run side-by-side, one
  per target.
- **Autosave** — drop `PersistenceKit.Autosave.AutoSaveLoop` on a GameObject and call
  `Bind(kit)`, or `AutoSaveLoop.Install(kit)` from code. Mutations debounce; quit and
  pause force-flush.
- **DI** — `RegisterPersistenceKit(builder => …)` (VContainer) or
  `Container.BindPersistenceKit(builder => …)` (Zenject). Both adapter asmdefs activate
  automatically when their target package is installed.

## Notes on encryption

The shipped `AesGcmEncryptor` uses **AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC)**, with a
fresh 16-byte IV per write and a 32-byte tag. Tokens look like
`enc:v1:<base64-iv>:<base64-ciphertext>:<base64-tag>` — the `v1` prefix is reserved so a
future cipher version can coexist.

Despite the class name, this is **not** AES-GCM: Unity's Mono runtime does not ship
`System.Security.Cryptography.AesGcm` on every platform (its constructor throws
`PlatformNotSupportedException`), so the kit pairs primitives the BCL supports everywhere.
CBC+HMAC in Encrypt-then-MAC form matches AES-GCM's guarantees against the offline attacker
this kit is designed for: the tag covers `IV ‖ ciphertext` and is verified in constant time
*before* any decryption, so there is no padding oracle. The class name is retained for ABI
stability.

`ConstantKeyProvider` is a development convenience. For production, derive keys from a
device keystore / user secret rather than embedding them in the build.

## Per-field warnings

The analyzer emits diagnostics PK001..PK007. The most common ones:

- `PK001` — the class is missing the `partial` modifier.
- `PK002` — `[Encrypted]` on a `Remote`-targeted field. The kit can't inspect remote
  payloads; encryption usually pairs better with a Json/Binary target.
- `PK005` — `[Persist]` on a `static`/`const`/`readonly` field.
- `PK007` — `[PersistentState]` without `partial`.

## Layout

```
Assets/PersistenceKit/
├── Runtime/
│   ├── Core/           interfaces, manager, registry, dirty tracker, encryptor, builder
│   ├── Targets/        JsonDiskTarget, BinaryDiskTarget, PlayerPrefsTarget, RemoteTarget
│   ├── Serializers.Newtonsoft/   default JSON handler
│   ├── Serializers.SystemTextJson/ optional STJ handler (gate: PERSISTENCEKIT_STJ)
│   ├── Autosave/       AutoSaveLoop MonoBehaviour
│   └── Plugins/        source generator DLL (RoslynAnalyzer label)
├── Editor/
│   ├── PersistenceKitWindow.cs / .uss      Window → PersistenceKit → Inspector
│   ├── Inspectors/                          reflection + activity-log helpers
│   └── Tabs/                                Inspector / Storage / Activity tabs
├── DI/
│   ├── VContainer/     extension method (active iff VContainer installed)
│   └── Zenject/        extension method (active iff Zenject/Extenject installed)
├── SourceGenerator~/   generator csproj — Unity ignores the trailing-~ folder
├── Samples/            BasicSaveSample.cs — copy into your project to try it out
└── Tests/
    └── Runtime/        EditMode + PlayMode tests
```
