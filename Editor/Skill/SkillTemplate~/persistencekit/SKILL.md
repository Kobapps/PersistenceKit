---
name: persistencekit
description: >-
  Integrate and use the PersistenceKit Unity save system correctly. Use this skill
  whenever you are adding save/load, persistence, or serialized game state to a Unity
  project that has PersistenceKit installed — declaring a [PersistentState] class, marking
  fields with [Persist] / [Encrypted], wiring PersistenceKitBuilder, routing state to
  Json / Binary / PlayerPrefs / Remote targets, adding autosave, encrypting save data,
  slots/profiles, cloud saves, or registering the kit with VContainer/Zenject. Trigger it
  even when the user says only "save the player's progress", "persist settings", "add a
  save system", "store this in PlayerPrefs", or "encrypt the auth token" in a Unity
  context — PersistenceKit has non-obvious conventions (partial classes, MarkDirty, one
  serializer per target, checkpoint saves) that are easy to get wrong without this guide.
---

# Integrating PersistenceKit

PersistenceKit is a field-level, source-generated save system for Unity 6. You mark
fields with `[Persist]`, route each independently to a storage target, and the generator
emits the boilerplate. This skill gets an integration right the first time and steers
around the sharp edges.

Read this file top-to-bottom for a normal integration. For depth, open the reference
files as noted:

- `references/api.md` — full API surface (builder, manager, targets, serializers, encryption, DI).
- `references/patterns.md` — recipes: slots/profiles, autosave, cloud, migration, checkpoints.
- `references/pitfalls.md` — the mistakes that cause data loss or "it compiles but nothing saves".

## The mental model (read this first)

Three moving parts, and getting them straight prevents most bugs:

1. **State classes** — plain `partial` classes tagged `[PersistentState]`. Fields you tag
   `[Persist]` are saved; everything else is ignored. Each field is routed to exactly one
   **target**.
2. **The `PersistenceManager`** — built once at startup from a `PersistenceKitBuilder`.
   It owns the target backends, the serializers, the encryptor, and a cache of loaded
   states. It is long-lived (one per app, or one per DI scope). You load/save through it.
3. **Dirty tracking** — a save writes only the targets that have unsaved changes. Fields
   go dirty either automatically (property convention) or when you call `MarkDirty()`
   (direct-field convention). `SaveAsync` flushes the dirty targets and clears their flags.

The single most important rule: **every target you route a field to needs BOTH a target
backend AND a serializer wired in the builder.** A field routed to `Binary` with no
`UseSerializer(PersistTarget.Binary, …)` throws at save time.

## Step 1 — Declare the state

Prefer the **direct-field convention** for new code (the field name is the access path):

```csharp
using PersistenceKit;
using System.Collections.Generic;

[PersistentState]                       // class MUST be partial — the generator adds the other half
public partial class PlayerState
{
    [Persist]                                    public string UserId;        // default target
    [Persist(target: PersistTarget.PlayerPrefs)] public int    SessionCount;  // routed explicitly
    [Persist(target: PersistTarget.Remote)]      public string CloudTag;
    [Persist, Encrypted]                         public string AuthToken;     // encrypted at rest
    [Persist]                                    public List<string> Unlocks; // collections are fine

    public float runtimeOnly;   // no [Persist] → never touched by the kit
}
```

Rules that matter:
- The class **must be `partial`**. If it isn't, the generator can't extend it and it won't
  implement `IPersistentState` — loads/saves won't compile.
- `[Persist]` with no argument uses the kit's **default target** (set via
  `UseDefaultTarget`, falls back to `Json`). `[Persist(target: …)]` overrides per field.
- `[Encrypted]` encrypts that leaf at serialization time; the in-memory value stays
  plaintext. Requires an encryptor to be wired or it throws at runtime.
- After **direct-field writes you must call `MarkDirty()`** — the field itself has no
  setter hook. See Step 4.

The **property convention** (legacy) uses `private` underscore fields and the generator
emits a public PascalCase property whose setter auto-dirties — see `references/api.md`.
Don't mix conventions on the same field.

## Step 2 — Wire the kit once at startup

Build a single `PersistenceManager` and keep it alive (a bootstrap MonoBehaviour, a DI
singleton — not per-scene, not per-call):

```csharp
using PersistenceKit;
using PersistenceKit.Targets;
using PersistenceKit.Serializers;   // NewtonsoftJsonHandler

var kit = PersistenceKitBuilder.Default()
    .UseDefaultTarget(PersistTarget.Json)
    .UseTarget(PersistTarget.Json,        new JsonDiskTarget())
    .UseTarget(PersistTarget.Binary,      new BinaryDiskTarget())
    .UseTarget(PersistTarget.PlayerPrefs, new PlayerPrefsTarget())
    .UseTarget(PersistTarget.Remote,      new RemoteTarget(new InMemoryRemoteProvider()))
    .UseSerializer(PersistTarget.Json,        new NewtonsoftJsonHandler())
    .UseSerializer(PersistTarget.Binary,      new NewtonsoftJsonHandler())
    .UseSerializer(PersistTarget.PlayerPrefs, new NewtonsoftJsonHandler())
    .UseSerializer(PersistTarget.Remote,      new NewtonsoftJsonHandler())
    .UseEncryptor(new AesGcmEncryptor(new ConstantKeyProvider(key32)))  // only if you use [Encrypted]
    .Build();
```

- Only wire the targets you actually route fields to — but if you wire a target, wire its
  serializer too. `Build()` validates the configuration and throws early if it's incomplete.
- `NewtonsoftJsonHandler` needs `com.unity.nuget.newtonsoft-json` (≥ 3.0.0) in the project;
  the asmdef's version-define enables it automatically. `SystemTextJsonHandler` is an
  alternative (see `references/api.md`).
- Reuse the same serializer instance across targets — it's stateless and thread-safe.
- Using DI? Prefer `RegisterPersistenceKit` (VContainer) / `BindPersistenceKit` (Zenject)
  instead of hand-building — see `references/api.md` and `references/patterns.md`.

## Step 3 — Load

```csharp
var player = await kit.LoadOrCreateAsync<PlayerState>();          // default slot
var slotA  = await kit.LoadOrCreateAsync<PlayerState>("save-a");  // named slot / profile
```

`LoadOrCreateAsync` returns a cached instance keyed by `(Type, slot)` — call it again with
the same type+slot and you get the **same reference**, already populated from storage (or a
fresh default if nothing is stored). Cache the reference; don't reload every frame.

## Step 4 — Mutate, mark dirty, save

```csharp
player.UserId       = "kobi";     // direct-field writes do NOT auto-dirty
player.SessionCount = 12;
player.MarkDirty();               // flush every target in this state's mask
// or target a single backend:
player.MarkDirty(PersistTarget.PlayerPrefs);

await kit.SaveAsync(player);      // writes only the dirty targets, then clears their flags
```

- **Direct-field convention → you own `MarkDirty()`.** Forgetting it is the #1 "why isn't
  it saving?" bug. Property convention auto-dirties on the setter.
- `SaveAsync(state)` is a no-op if nothing is dirty. `SaveAllAsync()` saves every cached
  state that has pending writes.
- For anything the player would be upset to lose, **save at explicit checkpoints** — don't
  rely solely on autosave or the app-quit flush (see `references/pitfalls.md`).

For hands-off saving, install the debounced autosave loop instead of calling `SaveAsync`
by hand:

```csharp
using PersistenceKit.Autosave;
AutoSaveLoop.Install(kit, debounceSeconds: 0.5f);  // coalesces bursts of MarkDirty into one save
```

## Step 5 — Verify in the editor (use the Unity MCP)

Source generators fail silently in a text editor — always confirm the integration compiled
and the generator ran. When a Unity MCP is connected (`mcp__UnityMCP__*` /
`mcp__unity-mcp__*`), do this rather than guessing:

1. After editing state classes, trigger a refresh/compile and **read the console** —
   `read_console` filtered to errors/warnings. Look for:
   - C# compile errors (missing `partial`, wrong namespace, unwired target).
   - Generator diagnostics (they surface as warnings/errors prefixed by the analyzer).
   - The runtime error `No serializer/target wired for <Target>` if the builder is incomplete.
2. Open **Window → PersistenceKit → Inspector** and enter Play mode (`manage_editor` play).
   The manager and its loaded states appear in the tree; the Storage tab shows the bytes on
   disk; the Activity tab logs each save. If the tree is empty, no `PersistenceManager` was
   built at runtime — check your bootstrap actually runs.
3. If the project ships the Rich Save Sample, it's a working reference wiring — read it.

If the generated partial never appears (state class shows no `IPersistentState`), the
source-generator DLL may not be built/installed — see `references/pitfalls.md`.

## Best-practice checklist

- One long-lived `PersistenceManager`; register as a DI singleton where a container exists.
- Route by cost/semantics: tiny scalars/settings → `PlayerPrefs`; bulk/complex → `Json` or
  `Binary` disk; account/cloud → `Remote`. Don't put large blobs in PlayerPrefs.
- `[Encrypted]` only for secrets (tokens, entitlements). In production derive the key from
  the device keystore via a custom `IKeyProvider` — `ConstantKeyProvider` with a literal key
  is for samples/tests only, never shipped.
- `await` your saves and honor `CancellationToken`s; saving is real async I/O.
- Save at meaningful checkpoints (level end, purchase, settings change), not only on quit.
- Keep persisted types serializer-friendly: plain fields, `[Serializable]` nested classes,
  `List<>`/`Dictionary<,>`/`HashSet<>`. Avoid `UnityEngine.Object` references in state.
- Never touch `PlayerPrefs` yourself from a background thread; let the target marshal it.

When in doubt about an API shape, open `references/api.md` — don't invent method names.
