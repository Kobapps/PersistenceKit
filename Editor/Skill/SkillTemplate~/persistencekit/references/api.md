# PersistenceKit API Reference

Namespaces: `PersistenceKit` (core), `PersistenceKit.Targets`, `PersistenceKit.Serializers`
(via the Newtonsoft/SystemTextJson asmdefs), `PersistenceKit.Autosave`, `PersistenceKit.DI`.

## Contents
- [Attributes](#attributes)
- [Declaring state — two conventions](#declaring-state--two-conventions)
- [PersistenceKitBuilder](#persistencekitbuilder)
- [PersistenceManager](#persistencemanager)
- [Targets](#targets)
- [Serializers](#serializers)
- [Encryption](#encryption)
- [AutoSaveLoop](#autosaveloop)
- [Dependency injection](#dependency-injection)
- [Custom targets / serializers / providers](#extending)

## Attributes

| Attribute | Applies to | Purpose |
|---|---|---|
| `[PersistentState]` | class | Marks a class for the generator. Optional `TypeId = "…"` overrides the storage key (defaults to the class name). Class must be `partial`. |
| `[Persist]` | field | Save this field to the **default** target. |
| `[Persist(PersistTarget.X)]` | field | Save this field to target `X`. Optional `Name = "…"` overrides the serialized key. |
| `[Encrypted]` | field | Encrypt this field's value at rest. Optional `KeyPurpose = "…"` (default `"default"`) selects the key from `IKeyProvider`. |

`PersistTarget` enum: `Json`, `Binary`, `PlayerPrefs`, `Remote`.
`PersistTargetMask` is the `[Flags]` bitmask form (a state's `TargetMask` is the union of its fields' targets).

## Declaring state — two conventions

**Direct fields (recommended).** The field is the access path; you call `MarkDirty()`:

```csharp
[PersistentState]
public partial class Profile
{
    [Persist] public string DisplayName;
    [Persist(target: PersistTarget.PlayerPrefs)] public int Level;
}
// usage: profile.Level = 5; profile.MarkDirty();  (or MarkDirty(PersistTarget.PlayerPrefs))
```

**Property convention (legacy).** Underscore-prefixed `private` field; the generator emits a
public PascalCase property whose setter auto-marks dirty:

```csharp
[PersistentState]
public partial class Profile
{
    [Persist] private string _displayName;   // → public string DisplayName { get; set; }
    [Persist(target: PersistTarget.PlayerPrefs)] private int _level;
}
// usage: profile.DisplayName = "kobi";  // setter dirties the Json target automatically
```

Don't mix the two on one field. Nested types held by a persisted field should be plain
`[Serializable]` classes/structs or standard collections (`List<>`, `Dictionary<,>`,
`HashSet<>`). No `UnityEngine.Object` references.

If you can't or don't want to run the generator, you can hand-implement `IPersistentState`;
see `Tests/Runtime/Fixtures/TestStates.cs` in the package. Generated and hand-written shapes
are interoperable.

## PersistenceKitBuilder

Fluent; each method returns the builder. Terminate with `Build()`.

| Method | Notes |
|---|---|
| `PersistenceKitBuilder.Default()` | Start an empty builder (same as `Empty()`). |
| `.UseDefaultTarget(PersistTarget)` | Target for bare `[Persist]`. Falls back to `Json`. |
| `.UseTarget(PersistTarget, IPersistenceTarget)` | Wire a backend. The impl's `.Target` must match. |
| `.UseSerializer(PersistTarget, ISerializerHandler)` | Wire a serializer for that target. **Required for every wired target.** |
| `.UseEncryptor(IEncryptor)` | Install encryption. Required only if any field is `[Encrypted]`. |
| `.Build()` | Validates config, resolves default targets, returns a `PersistenceManager`. Throws on incomplete config. |

## PersistenceManager

Long-lived. `IDisposable`. Thread-aware (uses `ConfigureAwait(false)` internally).

| Member | Signature | Notes |
|---|---|---|
| Load | `ValueTask<T> LoadOrCreateAsync<T>(string slot = "", CancellationToken = default)` | Cached by `(T, slot)`; returns same reference on repeat. Creates a default if nothing stored. |
| Save one | `ValueTask SaveAsync(IPersistentState state, CancellationToken = default)` | Writes only dirty targets; no-op if clean. Clears dirty flags for targets it confirms written; **re-marks dirty on write failure** so the change retries (not silently lost). |
| Save all | `ValueTask SaveAllAsync(CancellationToken = default)` | Iterates the cache, saving each dirty state. |
| Delete | `ValueTask DeleteAsync<T>(string slot = "", CancellationToken = default)` | Deletes from every target in the type's mask and evicts from cache. |
| Is loaded | `bool IsLoaded<T>(string slot = "")` | Cache membership check. |
| Evict | `void Evict<T>(string slot = "")` | Drop a cached instance without touching storage. |
| Dirty tracker | `DirtyTracker Dirty { get; }` | Exposed for autosave wiring. |
| Options | `PersistenceKitOptions Options { get; }` | Read-only inspection. |
| Events | `event Action<SaveEvent> OnSaved` | Fires after each target write. **May fire on a threadpool thread** — marshal to main before touching Unity APIs. |
| Stats | `long SaveCount`, `long BytesSaved` | Counters. |
| Introspection | `List<IPersistentState> SnapshotCache()`, `static List<PersistenceManager> ActiveManagers` | Used by the editor window. |

`IPersistentState` (on every state): `string Key`, `PersistTargetMask TargetMask`,
`void MarkDirty()`, `void MarkDirty(PersistTarget)`.

## Targets

All implement `IPersistenceTarget` (`SaveAsync` / `LoadAsync` / `DeleteAsync` / `Exists`).

| Target | Constructor | Storage | Notes |
|---|---|---|---|
| `JsonDiskTarget` | `()` or `(string rootDir)` | `<persistentDataPath>/PersistenceKit/json/<key>.json` | Atomic write via temp+rename, fsync for durability. |
| `BinaryDiskTarget` | `()` or `(string rootDir)` | `…/PersistenceKit/…/<key>.bin` | Same disk plumbing as Json. |
| `PlayerPrefsTarget` | `(long soft=1MB, long hard=2MB)` | `PlayerPrefs` key `pk:<key>` (base64) | Small values only — warns past soft, throws past hard limit. Marshals to the main thread automatically. |
| `RemoteTarget` | `(IRemotePersistenceProvider provider)` | Whatever the provider backs | `InMemoryRemoteProvider` is a stub for testing; supply your own for real cloud. |

The serializer produces the bytes; the target only does I/O. So the same `NewtonsoftJsonHandler`
can back a `BinaryDiskTarget` (you get JSON bytes in a `.bin`) — routing is about *where*, the
serializer is about *how*.

## Serializers

Implement `ISerializerHandler`.

| Handler | Constructor | Requires |
|---|---|---|
| `NewtonsoftJsonHandler` | `(bool indent = false, JsonSerializerSettings settings = null)` | `com.unity.nuget.newtonsoft-json` ≥ 3.0.0 — auto-enables `PERSISTENCEKIT_NEWTONSOFT` via the asmdef version-define. |
| `SystemTextJsonHandler` | `(bool indent = false, JsonSerializerOptions options = null)` | Opt-in: the handler is guarded by `#if PERSISTENCEKIT_STJ`, which you must add to Scripting Define Symbols yourself (the asmdef also has a matching define constraint). |

Newtonsoft is the default and handles `Dictionary<,>` / `HashSet<>` / polymorphic nesting most
robustly. Serializer instances are stateless — share one across targets.

## Encryption

- `IEncryptor` — the encrypt/decrypt contract. `AesGcmEncryptor(IKeyProvider keys)` is the
  bundled implementation (**note:** despite the name it currently implements AES-256-CBC +
  HMAC-SHA256; the on-disk token is `enc:v1:<iv>:<ct>:<tag>`).
- `IKeyProvider` — supplies 32-byte keys by purpose. `ConstantKeyProvider(byte[] key32)` holds
  one key; `.WithKey("purpose", key)` adds purpose-specific keys.
- **Production:** implement `IKeyProvider` to fetch/derive keys from the platform keystore
  (Keychain / Keystore / DPAPI). Never ship a hardcoded key. `ConstantKeyProvider` with a
  literal key is for samples and tests.

## AutoSaveLoop

`PersistenceKit.Autosave.AutoSaveLoop` — optional MonoBehaviour that debounces saves.

- `static AutoSaveLoop Install(PersistenceManager kit, float debounceSeconds = 0.5f)` — creates
  a hidden `DontDestroyOnLoad` GameObject bound to the kit. Mutations within the debounce
  window collapse into one `SaveAllAsync`.
- `Bind(PersistenceManager)`, `Pause()`/`Resume()`, `Stop()`/`StartLoop()`, and the process-wide
  `PauseAll()/ResumeAll()/StopAll()/StartAll()` + `AggregateStatus()`.
- It force-flushes on `OnApplicationPause(true)` / `OnApplicationQuit`, but that flush is
  **best-effort** (see `pitfalls.md`) — pair it with explicit checkpoint saves.

## Dependency injection

Adapters compile only when the container package is present.

**VContainer** (`PERSISTENCEKIT_VCONTAINER`):
```csharp
builder.RegisterPersistenceKit(kit => kit
    .UseDefaultTarget(PersistTarget.Json)
    .UseTarget(PersistTarget.Json, new JsonDiskTarget())
    .UseSerializer(PersistTarget.Json, new NewtonsoftJsonHandler()));
// PersistenceManager + DirtyTracker are registered as singletons.
```

**Zenject** (`PERSISTENCEKIT_ZENJECT`):
```csharp
Container.BindPersistenceKit(kit => kit
    .UseDefaultTarget(PersistTarget.Json)
    .UseTarget(PersistTarget.Json, new JsonDiskTarget())
    .UseSerializer(PersistTarget.Json, new NewtonsoftJsonHandler()));
// Binds PersistenceManager + DirtyTracker AsSingle.
```

## Extending

- **Custom cloud backend:** implement `IRemotePersistenceProvider` (use `UnityWebRequest`,
  not `HttpClient`, for WebGL compatibility) and pass it to `new RemoteTarget(provider)`.
- **Custom serializer:** implement `ISerializerHandler` (`Serialize`/`Deserialize` over the
  payload reader/writer, honoring the encryptor for `[Encrypted]` leaves).
- **Custom target:** implement `IPersistenceTarget`. Keep `Exists` cheap (no payload alloc)
  and make writes atomic + durable if the data matters.
