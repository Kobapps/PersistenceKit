# PersistenceKit Patterns & Recipes

Copy-adaptable solutions for common integrations. All assume a single long-lived `kit`
(`PersistenceManager`) built at startup (see SKILL.md Step 2).

## Bootstrap: one manager for the whole app

Without a DI container, build the kit in a bootstrap MonoBehaviour that survives scene loads:

```csharp
public sealed class SaveBootstrap : MonoBehaviour
{
    public static PersistenceManager Kit { get; private set; }

    private void Awake()
    {
        if (Kit != null) { Destroy(gameObject); return; }   // singleton guard
        DontDestroyOnLoad(gameObject);
        Kit = PersistenceKitBuilder.Default()
            .UseDefaultTarget(PersistTarget.Json)
            .UseTarget(PersistTarget.Json, new JsonDiskTarget())
            .UseSerializer(PersistTarget.Json, new NewtonsoftJsonHandler())
            .Build();
        AutoSaveLoop.Install(Kit, debounceSeconds: 0.5f);
    }
}
```

Reference `SaveBootstrap.Kit` everywhere else. With VContainer/Zenject, register the kit as a
singleton instead and inject `PersistenceManager` — see `api.md`.

## Route fields by cost and meaning

Put each field where it belongs; one state class can span targets:

```csharp
[PersistentState]
public partial class GameState
{
    [Persist(PersistTarget.PlayerPrefs)] public float MasterVolume;   // tiny, frequent
    [Persist(PersistTarget.PlayerPrefs)] public bool  TutorialDone;
    [Persist(PersistTarget.Binary)]      public List<InventoryItem> Inventory;  // bulk
    [Persist(PersistTarget.Json)]        public PlayerProgress Progress;        // human-diffable
    [Persist(PersistTarget.Remote)]      public string AccountId;              // cloud
}
```

Guideline: scalars/settings → PlayerPrefs; large or complex → Binary/Json disk; account/cloud
→ Remote. Never route big blobs to PlayerPrefs (it warns past 1 MB, throws past 2 MB).

## Save slots / multiple profiles

The `slot` argument gives you independent instances of the same type:

```csharp
async Task<PlayerState> LoadSlot(int index) =>
    await kit.LoadOrCreateAsync<PlayerState>($"slot{index}");

// Each slot is its own cached instance and its own file (PlayerState:slot2.json, …).
var current = await LoadSlot(_activeSlot);

// Deleting a slot:
await kit.DeleteAsync<PlayerState>($"slot{index}");
```

## Explicit checkpoint save (do this for anything important)

Autosave is convenient but debounced and best-effort at quit. For progress the player would
hate to lose, save deliberately:

```csharp
public async void OnLevelComplete()
{
    _progress.LevelsCleared++;
    _progress.MarkDirty();
    await kit.SaveAsync(_progress);      // or kit.SaveAllAsync() to flush everything dirty
}
```

Good checkpoints: level/wave complete, purchase, meaningful settings change, entering a menu,
manual "Save" button.

## Autosave with pause control

```csharp
var loop = AutoSaveLoop.Install(kit, debounceSeconds: 0.4f);

// During a cutscene or loading screen where you don't want writes:
loop.Pause();
// …later:
loop.Resume();

// Process-wide (e.g., from a debug panel):
AutoSaveLoop.PauseAll();
AutoSaveLoop.ResumeAll();
```

## Encrypting secrets with a real key source

Never ship `ConstantKeyProvider` with a literal key. Implement `IKeyProvider` over the
platform keystore:

```csharp
public sealed class KeystoreKeyProvider : IKeyProvider
{
    public byte[] GetKey(string purpose)
    {
        // Fetch/derive a 32-byte key from Keychain (iOS) / Keystore (Android) /
        // DPAPI (Windows). Cache it; derive per-purpose with HKDF if you use KeyPurpose.
        return PlatformKeystore.GetOrCreate($"pk:{purpose}", length: 32);
    }
}

var kit = PersistenceKitBuilder.Default()
    // …targets + serializers…
    .UseEncryptor(new AesGcmEncryptor(new KeystoreKeyProvider()))
    .Build();

[PersistentState]
public partial class Account
{
    [Persist, Encrypted]                        public string SessionToken;  // "default" key
    [Persist, Encrypted(KeyPurpose = "billing")] public string ReceiptSig;   // separate key
}
```

The on-disk value is an `enc:v1:…` token; the plaintext never hits storage.

## Cloud saves with a real provider

`InMemoryRemoteProvider` is a test stub. For real cloud, implement
`IRemotePersistenceProvider` using `UnityWebRequest` (works on WebGL; `HttpClient` doesn't):

```csharp
public sealed class HttpRemoteProvider : IRemotePersistenceProvider
{
    // Implement against your backend with UnityWebRequest:
    public ValueTask<byte[]> GetAsync(string key, CancellationToken ct) { /* GET → bytes or null */ }
    public ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct) { /* PUT */ }
    public ValueTask DeleteAsync(string key, CancellationToken ct) { /* DELETE */ }
    public bool Exists(string key) { /* cheap existence check */ }
}

.UseTarget(PersistTarget.Remote, new RemoteTarget(new HttpRemoteProvider()))
.UseSerializer(PersistTarget.Remote, new NewtonsoftJsonHandler())
```

A remote write is slow I/O — always `await` it and pass a `CancellationToken` so you can abort
on scene teardown.

## Schema migration (adding/removing fields)

Adding a `[Persist]` field is safe: old saves simply lack the key, so it loads as the type
default. Removing a field: the old key in storage is ignored on load. For renames, keep the
serialized name stable with `[Persist(Name = "oldName")]` rather than renaming the key, or write
a one-time migration that loads, copies, and re-saves. Bump a persisted `int SchemaVersion`
field if you need to branch migration logic.

## Reset to defaults / new game

```csharp
public async Task NewGame()
{
    await kit.DeleteAsync<PlayerState>();      // wipe each cached state's storage
    await kit.DeleteAsync<GameState>();
    var player = await kit.LoadOrCreateAsync<PlayerState>();  // fresh defaults
}
```

The editor window's **Reset All** does the in-memory equivalent (fields → defaults + save)
for quick testing without deleting files.
