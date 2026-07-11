# PersistenceKit Pitfalls & Data-Safety Notes

Ordered roughly by how often they bite. Most "it compiles but nothing saves" reports are #1–#4.

## 1. Forgot `MarkDirty()` after a direct-field write

With the direct-field convention the field has no setter hook, so a plain assignment does
**not** schedule a save. `SaveAsync` then sees nothing dirty and no-ops.

```csharp
player.Coins += 100;          // nothing happens on save…
player.MarkDirty();           // …until you mark it
await kit.SaveAsync(player);
```

Property-convention fields (underscore backing field) auto-dirty on the setter, so this only
affects direct fields. If a value refuses to persist, check for a missing `MarkDirty()` first.

## 2. Class isn't `partial`

`[PersistentState]` classes must be `partial` — the generator emits the other half that
implements `IPersistentState`. Without `partial` the state won't satisfy the
`where T : IPersistentState` constraint and `LoadOrCreateAsync<T>` won't even compile.

## 3. Wired a target but not its serializer (or vice versa)

Every target a field routes to needs BOTH `UseTarget(...)` and `UseSerializer(...)`. Miss one
and the first save/load through that target throws:
`No serializer wired for Binary.` / `No target wired for Remote.` `Build()` catches some cases
early; the rest surface at first I/O. Match your builder's targets to the targets your
`[Persist(target: …)]` attributes actually reference.

## 4. Source-generator DLL not built / not installed

The generator's source lives in `Assets/PersistenceKit/SourceGenerator~/` (hidden by the `~`).
The **compiled** `PersistenceKit.SourceGenerator.dll` must be present under
`Assets/PersistenceKit/Runtime/Plugins/` with its `.meta` labelled `RoslynAnalyzer`. If it's
missing, no partials are generated: your state classes won't implement `IPersistentState`.
Build it (`SourceGenerator~/build.ps1`, then refresh Unity) or hand-implement `IPersistentState`
(see `Tests/Runtime/Fixtures/TestStates.cs`). Confirm via the Unity MCP `read_console` — the
generator reports diagnostics there.

## 5. The editor window is empty in Edit mode — that's expected

**Window → PersistenceKit → Inspector** is a *live* view of active `PersistenceManager`
instances. Managers are usually built at runtime, so the window populates in **Play mode**.
An empty tree in Edit mode is normal; an empty tree in Play mode means your bootstrap never
ran `Build()`. (Also note: package samples live in `Samples~`, which Unity ignores until you
import them via Package Manager — an un-imported sample won't run.)

## 6. Don't rely on the app-quit flush as your only save

`AutoSaveLoop` force-flushes on `OnApplicationQuit` / `OnApplicationPause(true)`, but that
flush is fire-and-forget (`async void`) and can be cut off by process teardown before the
write completes — and a blocking drain would deadlock because `PlayerPrefsTarget` marshals to
the main thread. **Treat quit-time save as best-effort.** Save at explicit checkpoints during
play (see `patterns.md`) so the last important change is already on disk before quit.

## 7. Threading: `OnSaved` and background writes

- `PersistenceManager.OnSaved` fires on the thread the write completed on — often a threadpool
  thread. If a subscriber touches `UnityEngine`/editor APIs, marshal to the main thread first.
- Never call `PlayerPrefs` yourself from a background thread. `PlayerPrefsTarget` already
  marshals its own operations; your own off-thread `PlayerPrefs` calls will throw.

## 8. PlayerPrefs size limits

`PlayerPrefsTarget` warns above ~1 MB and **throws** above ~2 MB per key. It's for small
values (settings, counters, flags). Route inventories, world state, and blobs to a disk target.

## 9. Types the serializer can't round-trip

Persisted fields should be plain data: primitives, strings, enums, `[Serializable]` nested
classes/structs, and `List<>` / `Dictionary<,>` / `HashSet<>`. Avoid:
- `UnityEngine.Object` references (GameObjects, ScriptableObjects, materials) — store an id and
  resolve it at load, don't persist the reference.
- Cyclic object graphs (unless your serializer settings handle references).
- Types with no parameterless constructor when relying on default construction.

## 10. Encryption keys

- `[Encrypted]` without `UseEncryptor(...)` throws at runtime. Wire an encryptor if any field
  is encrypted.
- Ship a real `IKeyProvider` backed by the platform keystore. A hardcoded
  `ConstantKeyProvider` key is trivially extractable from the build — samples/tests only.
- Losing/rotating the key makes existing encrypted values undecryptable. Plan key rotation
  (via `KeyPurpose` + a migration re-save) before you ship.
- The bundled `AesGcmEncryptor` is currently AES-256-CBC + HMAC-SHA256 despite the class name;
  fine for at-rest confidentiality + integrity, but don't describe it as GCM/AEAD in a security
  review.

## 11. WebGL specifics

- Disk writes are non-atomic on WebGL (IDBFS lacks reliable `File.Replace`), and IDBFS syncs to
  IndexedDB on Unity's schedule. For hard guarantees around a critical save, nudge a sync:
  `Application.ExternalEval("FS.syncfs(false, e => {});")`.
- A custom `IRemotePersistenceProvider` must use `UnityWebRequest` (not `HttpClient`).

## 12. Don't reload every frame

`LoadOrCreateAsync` returns a cached reference, but it's still an async call with a cache
lookup. Load once at startup/scene-enter, cache the reference, mutate that. Reloading in
`Update` is wasteful and can race in-flight saves.
