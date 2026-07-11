using System;
using System.Collections.Generic;
using System.Text;
using PersistenceKit.Autosave;
using PersistenceKit.Targets;
using UnityEngine;
using UnityEngine.UI;

namespace PersistenceKit.Samples
{
    /// <summary>
    /// Comprehensive sample driver — exercises four state types across all four targets,
    /// encryption, and multi-slot rotation. The companion editor scene builder wires a
    /// vertical column of buttons to each public method below; mutations show up live in
    /// the status pane and in <c>Window → PersistenceKit → Inspector</c>.
    /// </summary>
    public sealed class RichSaveSample : MonoBehaviour
    {
        // Set by the scene builder. Kept public so the user can re-target the label.
        public Text StatusLabel;

        [Tooltip("Auto-save debounce window. Mutations within this window collapse to one save.")]
        [SerializeField] private float _debounceSeconds = 0.4f;

        /// <summary>Editor-only knob: read by SampleSceneBuilder to apply the kit's project setting.</summary>
        public float DebounceSeconds { get => _debounceSeconds; set => _debounceSeconds = value; }

        private static readonly string[] Slots = { "slot1", "slot2", "slot3" };
        private int _slotIndex;

        private PersistenceManager _kit;
        private PlayerProfile      _profile;
        private InventoryState     _inventory;
        private SettingsState      _settings;
        private CloudState         _cloud;

        private async void Start()
        {
            _kit = BuildKit();
            AutoSaveLoop.Install(_kit, debounceSeconds: _debounceSeconds);

            _profile   = await _kit.LoadOrCreateAsync<PlayerProfile>(Slots[_slotIndex]);
            _inventory = await _kit.LoadOrCreateAsync<InventoryState>();
            _settings  = await _kit.LoadOrCreateAsync<SettingsState>();
            _cloud     = await _kit.LoadOrCreateAsync<CloudState>();

            EnsureProfileDefaults(_profile);
            if (_inventory.Items == null) _inventory.Items = new List<InventoryItem>();
            if (string.IsNullOrEmpty(_settings.Language))  _settings.Language = "en-US";
            if (_settings.MusicVolume == 0f && _settings.SfxVolume == 0f)
            {
                _settings.MusicVolume = 0.7f;
                _settings.SfxVolume   = 0.8f;
            }

            UpdateStatus();
        }

        private void Update()
        {
            // Refresh on every frame — keeps the on-screen status pane in lockstep with
            // mutations from buttons, the editor inspector, or external code.
            if (_kit != null) UpdateStatus();
        }

        // ─── Mutations (one per button) ──────────────────────────

        public void MutateProfile()
        {
            _profile.DisplayName = "Player_" + UnityEngine.Random.Range(1000, 9999);
            _profile.Level       += 1;
            _profile.Xp          += UnityEngine.Random.Range(50, 250);
            _profile.LastSeenUtc = DateTime.UtcNow.Ticks;
            _profile.AvatarSeed  = Guid.NewGuid().ToString("N").Substring(0, 8);
            // Direct-field convention: flush all targets in this state's mask in one call.
            _profile.MarkDirty();
            UpdateStatus();
        }

        public void RotateAuthToken()
        {
            // Encrypted leaf — observe in Storage that the on-disk JSON contains an
            // "enc:v1:..." token, never the plaintext.
            _profile.AuthToken = "tok_" + Guid.NewGuid().ToString("N").Substring(0, 16);
            _profile.MarkDirty(PersistTarget.Json);   // AuthToken lives on the default (Json) target
            UpdateStatus();
        }

        public void AddItem()
        {
            if (_inventory == null) return;
            if (_inventory.Items == null) _inventory.Items = new List<InventoryItem>();

            var rarity = (ItemRarity)UnityEngine.Random.Range(0, 4);
            _inventory.Items.Add(new InventoryItem
            {
                Id          = "item_" + UnityEngine.Random.Range(0, 0xFFFF).ToString("X4"),
                Count       = UnityEngine.Random.Range(1, 50),
                Rarity      = rarity,
                AcquiredUtc = DateTime.UtcNow.Ticks,
            });
            _inventory.LastLootedUtc = DateTime.UtcNow.Ticks;
            _inventory.MarkDirty();
            UpdateStatus();
        }

        public void RemoveLastItem()
        {
            if (_inventory?.Items == null || _inventory.Items.Count == 0) return;
            _inventory.Items.RemoveAt(_inventory.Items.Count - 1);
            _inventory.MarkDirty();
            UpdateStatus();
        }

        public void AddCoins()
        {
            _inventory.Coins += UnityEngine.Random.Range(50, 500);
            _inventory.MarkDirty(PersistTarget.Binary);
            UpdateStatus();
        }

        public void AddGems()
        {
            _inventory.Gems += UnityEngine.Random.Range(1, 10);
            _inventory.MarkDirty(PersistTarget.Binary);
            UpdateStatus();
        }

        public void SwitchSlot()
        {
            _slotIndex = (_slotIndex + 1) % Slots.Length;
            // Dispatch the load — when it resolves, the cached instance for the new slot
            // is bound. Other states (inventory/settings/cloud) are slot-independent.
            LoadProfileSlot();
        }

        private async void LoadProfileSlot()
        {
            try
            {
                _profile = await _kit.LoadOrCreateAsync<PlayerProfile>(Slots[_slotIndex]);
                EnsureProfileDefaults(_profile);
            }
            catch (Exception ex) { Debug.LogException(ex); }
            UpdateStatus();
        }

        /// <summary>
        /// Initialise nullable collection fields so the mutator buttons can mutate without
        /// a null check. Runs after every profile load (Start + every slot switch).
        /// </summary>
        private static void EnsureProfileDefaults(PlayerProfile p)
        {
            if (p == null) return;
            if (p.Stats        == null) p.Stats        = new Dictionary<string, int>();
            if (p.Achievements == null) p.Achievements = new HashSet<string>();
        }

        public void ToggleVibration()
        {
            _settings.VibrationEnabled = !_settings.VibrationEnabled;
            _settings.MarkDirty();
            UpdateStatus();
        }

        public void AdjustVolume()
        {
            _settings.MusicVolume = UnityEngine.Random.Range(0f, 1f);
            _settings.SfxVolume   = UnityEngine.Random.Range(0f, 1f);
            _settings.MarkDirty();
            UpdateStatus();
        }

        public void CycleLanguage()
        {
            string[] langs = { "en-US", "es-ES", "fr-FR", "de-DE", "ja-JP" };
            int next = (Array.IndexOf(langs, _settings.Language) + 1) % langs.Length;
            if (next < 0) next = 0;
            _settings.Language = langs[next];
            _settings.MarkDirty();
            UpdateStatus();
        }

        public void BumpStat()
        {
            if (_profile == null) return;
            EnsureProfileDefaults(_profile);    // import/rollback may have nulled the dict

            string[] keys = { "kills", "deaths", "assists", "wins", "matches", "gold_earned", "play_time" };
            var k = keys[UnityEngine.Random.Range(0, keys.Length)];
            _profile.Stats.TryGetValue(k, out var cur);
            _profile.Stats[k] = cur + UnityEngine.Random.Range(1, 25);
            _profile.MarkDirty();
            UpdateStatus();
        }

        public void UnlockAchievement()
        {
            if (_profile == null) return;
            EnsureProfileDefaults(_profile);    // import/rollback may have nulled the set

            string[] pool = {
                "first_blood", "double_kill", "triple_kill", "monster_kill", "godlike",
                "loot_hoarder", "no_damage_run", "speed_demon", "completionist",
            };
            var pick = pool[UnityEngine.Random.Range(0, pool.Length)];
            if (_profile.Achievements.Add(pick))   // false if already unlocked — no dirty needed
                _profile.MarkDirty();
            UpdateStatus();
        }

        public void ClearStats()
        {
            if (_profile == null) return;
            EnsureProfileDefaults(_profile);

            _profile.Stats.Clear();
            _profile.Achievements.Clear();
            _profile.MarkDirty();
            UpdateStatus();
        }

        public void TouchCloud()
        {
            _cloud.LastSyncUtc   = DateTime.UtcNow.Ticks;
            _cloud.ServerVersion = UnityEngine.Random.Range(1, 100);
            _cloud.Region        = new[] { "us-east", "us-west", "eu-west", "ap-south" }[UnityEngine.Random.Range(0, 4)];
            _cloud.UserIdHash    = Guid.NewGuid().ToString("N").Substring(0, 12);
            _cloud.MarkDirty();
            UpdateStatus();
        }

        public async void SaveAllNow()
        {
            await _kit.SaveAllAsync();
            UpdateStatus();
            Debug.Log("[PersistenceKit sample] SaveAll completed.");
        }

        public async void WipeAll()
        {
            // Hard reset across every cached state. The kit deletes from each target in the
            // type's mask; PlayerPrefs entries, on-disk files, and the in-memory remote
            // dictionary all clear. Useful when demoing the "fresh launch" path.
            await _kit.DeleteAsync<PlayerProfile>(Slots[_slotIndex]);
            await _kit.DeleteAsync<InventoryState>();
            await _kit.DeleteAsync<SettingsState>();
            await _kit.DeleteAsync<CloudState>();

            _profile   = await _kit.LoadOrCreateAsync<PlayerProfile>(Slots[_slotIndex]);
            _inventory = await _kit.LoadOrCreateAsync<InventoryState>();
            _settings  = await _kit.LoadOrCreateAsync<SettingsState>();
            _cloud     = await _kit.LoadOrCreateAsync<CloudState>();
            EnsureProfileDefaults(_profile);
            if (_inventory.Items == null) _inventory.Items = new List<InventoryItem>();
            UpdateStatus();
        }

        // ─── Status pane ─────────────────────────────────────────

        private void UpdateStatus()
        {
            if (StatusLabel == null) return;

            var sb = new StringBuilder(512);
            sb.AppendLine($"<b>SLOT</b>  {Slots[_slotIndex]}    (cycle with Switch Slot)");
            sb.AppendLine();

            sb.AppendLine("<b>PlayerProfile</b>  (Json + PlayerPrefs + encrypted token)");
            sb.AppendLine($"  Name:    {Safe(_profile?.DisplayName)}");
            sb.AppendLine($"  Level:   {_profile?.Level}    XP: {_profile?.Xp}");
            sb.AppendLine($"  Avatar:  {Safe(_profile?.AvatarSeed)}");
            sb.AppendLine($"  Token:   {(string.IsNullOrEmpty(_profile?.AuthToken) ? "<unset>" : "********")}");
            sb.AppendLine($"  Seen:    {FormatTicks(_profile?.LastSeenUtc ?? 0)}");
            if (_profile?.Stats != null && _profile.Stats.Count > 0)
            {
                sb.Append("  Stats:   ");
                int n = 0;
                foreach (var kv in _profile.Stats)
                {
                    if (n > 0) sb.Append(", ");
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                    if (++n >= 4) { sb.Append("…"); break; }
                }
                sb.AppendLine();
            }
            if (_profile?.Achievements != null && _profile.Achievements.Count > 0)
            {
                sb.AppendLine($"  Achv:    {_profile.Achievements.Count} unlocked");
            }
            sb.AppendLine();

            sb.AppendLine("<b>InventoryState</b>  (Binary disk)");
            sb.AppendLine($"  Coins:   {_inventory?.Coins}    Gems: {_inventory?.Gems}");
            sb.AppendLine($"  Items:   {_inventory?.Items?.Count ?? 0}");
            if (_inventory?.Items != null)
            {
                int show = Math.Min(3, _inventory.Items.Count);
                for (int i = _inventory.Items.Count - show; i < _inventory.Items.Count; i++)
                {
                    var it = _inventory.Items[i];
                    sb.AppendLine($"    · {it.Id} ×{it.Count} ({it.Rarity})");
                }
                if (_inventory.Items.Count > show)
                    sb.AppendLine($"    … +{_inventory.Items.Count - show} more");
            }
            sb.AppendLine();

            sb.AppendLine("<b>SettingsState</b>  (PlayerPrefs)");
            sb.AppendLine($"  Lang:    {Safe(_settings?.Language)}");
            sb.AppendLine($"  Music:   {_settings?.MusicVolume:F2}    SFX: {_settings?.SfxVolume:F2}");
            sb.AppendLine($"  Vibrate: {_settings?.VibrationEnabled}");
            sb.AppendLine();

            sb.AppendLine("<b>CloudState</b>  (Remote, in-memory provider)");
            sb.AppendLine($"  User:    {Safe(_cloud?.UserIdHash)}");
            sb.AppendLine($"  Region:  {Safe(_cloud?.Region)}");
            sb.AppendLine($"  Server:  v{_cloud?.ServerVersion}");
            sb.AppendLine($"  Sync:    {FormatTicks(_cloud?.LastSyncUtc ?? 0)}");
            sb.AppendLine();

            sb.AppendLine($"<b>Manager</b>  saves: {_kit?.SaveCount}    bytes: {_kit?.BytesSaved}");
            StatusLabel.text = sb.ToString();
        }

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "<unset>" : s;
        private static string FormatTicks(long ticks)
        {
            if (ticks == 0) return "<never>";
            try { return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss"); }
            catch { return "<bad ticks>"; }
        }

        private static PersistenceManager BuildKit()
        {
#if PERSISTENCEKIT_NEWTONSOFT
            var jsonHandler = (ISerializerHandler)new PersistenceKit.Serializers.NewtonsoftJsonHandler();
#else
            ISerializerHandler jsonHandler = null;
            Debug.LogError("[PersistenceKit sample] No serializer wired — install com.unity.nuget.newtonsoft-json or supply ISerializerHandler.");
#endif
            // Demo key — derive from device keystore in real apps.
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 7 ^ 0x5A);

            return PersistenceKitBuilder.Default()
                .UseDefaultTarget(PersistTarget.Json)
                .UseTarget(PersistTarget.Json,        new JsonDiskTarget())
                .UseTarget(PersistTarget.Binary,      new BinaryDiskTarget())
                .UseTarget(PersistTarget.PlayerPrefs, new PlayerPrefsTarget())
                .UseTarget(PersistTarget.Remote,      new RemoteTarget(new InMemoryRemoteProvider()))
                .UseSerializer(PersistTarget.Json,        jsonHandler)
                .UseSerializer(PersistTarget.Binary,      jsonHandler)
                .UseSerializer(PersistTarget.PlayerPrefs, jsonHandler)
                .UseSerializer(PersistTarget.Remote,      jsonHandler)
                .UseEncryptor(new AesGcmEncryptor(new ConstantKeyProvider(key)))
                .Build();
        }
    }
}
