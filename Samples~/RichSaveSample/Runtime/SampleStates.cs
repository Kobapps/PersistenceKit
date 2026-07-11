using System;
using System.Collections.Generic;

namespace PersistenceKit.Samples
{
    /// <summary>
    /// Profile state — a mix of all the kit's features in one class.
    ///   - DisplayName / Xp / LastSeenUtc / AvatarSeed → default target (Json on disk)
    ///   - Level → PlayerPrefs
    ///   - AuthToken → Json + AES encryption
    /// </summary>
    /// <remarks>
    /// Direct-field convention: fields are named exactly as the user accesses them
    /// (no underscore prefix, no generated property of a different name). After mutating
    /// fields directly, call <c>state.MarkDirty()</c> to flush pending changes.
    /// </remarks>
    [PersistentState]
    public partial class PlayerProfile
    {
        [Persist]                                       public string                    DisplayName;
        [Persist(target: PersistTarget.PlayerPrefs)]    public int                       Level;
        [Persist]                                       public int                       Xp;
        [Persist, Encrypted]                            public string                    AuthToken;
        [Persist]                                       public long                      LastSeenUtc;
        [Persist]                                       public string                    AvatarSeed;

        // Tracked stat counters — Dictionary survives Newtonsoft.Json round-trip and the
        // editor inspector renders it via the reflection drawer (read-only keys, editable
        // values, add/remove rows).
        [Persist] public Dictionary<string, int> Stats;

        // Set of unlocked achievement ids — HashSet renders with a flat item list and
        // add/remove buttons.
        [Persist] public HashSet<string> Achievements;
    }

    /// <summary>
    /// Inventory state — routed to the binary target. Demonstrates a complex collection
    /// payload (List of nested objects) round-tripping through the kit.
    /// </summary>
    [PersistentState]
    public partial class InventoryState
    {
        [Persist(target: PersistTarget.Binary)] public int                   Coins;
        [Persist(target: PersistTarget.Binary)] public int                   Gems;
        [Persist(target: PersistTarget.Binary)] public List<InventoryItem>   Items;
        [Persist(target: PersistTarget.Binary)] public long                  LastLootedUtc;
    }

    /// <summary>Application settings — cheap key/value pairs in PlayerPrefs.</summary>
    [PersistentState]
    public partial class SettingsState
    {
        [Persist(target: PersistTarget.PlayerPrefs)] public float  MusicVolume;
        [Persist(target: PersistTarget.PlayerPrefs)] public float  SfxVolume;
        [Persist(target: PersistTarget.PlayerPrefs)] public string Language;
        [Persist(target: PersistTarget.PlayerPrefs)] public bool   VibrationEnabled;
    }

    /// <summary>Cloud-only state — exercises the Remote target (in-memory provider in this sample).</summary>
    [PersistentState]
    public partial class CloudState
    {
        [Persist(target: PersistTarget.Remote)] public string UserIdHash;
        [Persist(target: PersistTarget.Remote)] public long   LastSyncUtc;
        [Persist(target: PersistTarget.Remote)] public int    ServerVersion;
        [Persist(target: PersistTarget.Remote)] public string Region;
    }

    [Serializable]
    public class InventoryItem
    {
        public string     Id;
        public int        Count;
        public ItemRarity Rarity;
        public long       AcquiredUtc;
    }

    public enum ItemRarity { Common, Rare, Epic, Legendary }
}
