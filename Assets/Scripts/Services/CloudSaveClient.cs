using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using Unity.Services.Authentication;

namespace Game.Net
{
    public class CloudSaveClient : MonoBehaviour
    {
        public static CloudSaveClient Instance { get; private set; }

        public struct PlayerConnectionLoadoutDTO
        {
            public byte version;      // bump if format changes
            public byte primary;      // maps to PrimaryType
            public byte secondary;    // maps to SecondaryType
            public byte melee;        // maps to MeleeType
            public byte utility;      // maps to UtilityType
        }

        public static event Action<PlayerConnectionLoadoutDTO> OnLoadoutReady; // fired as soon as Cloud Save returns

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Avoid creating a duplicate LoadoutHandshake if one already exists in the bootstrap scene.
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        if (FindFirstObjectByType<LoadoutHandshake>(FindObjectsInactive.Include) == null)
#else
        if (FindObjectOfType<LoadoutHandshake>() == null)
#endif
        {
            gameObject.AddComponent<LoadoutHandshake>();
            Debug.Log("[CloudSave] Added LoadoutHandshake (none found in scene).");
        }
        else
        {
            Debug.Log("[CloudSave] LoadoutHandshake already present; not adding another.");
        }
        // Use FindFirstObjectByType on Unity 6/2022.3+ (includes inactive) to silence deprecation and avoid duplicates.
    }        // Use simple, compliant key (alphanumeric only).
        const string Key = "LoadoutV1";
        const string StatsKey = "MatchStatsV1";
        static readonly string[] LegacyKeys = { "game.loadout.v1", "player.loadout" };

        public async Task<PlayerLoadout> LoadLoadoutAsync(PlayerLoadout fallback)
        {
            try
            {
                await EnsureReadyAsync();

                var primaryKeys = new HashSet<string> { Key };
                var resp = await CloudSaveService.Instance.Data.Player.LoadAsync(primaryKeys);
                if (resp != null && resp.TryGetValue(Key, out var item))
                {
                    if (TryParseLoadout(item.Value.GetAsString(), fallback, out var parsed))
                        return parsed;
                }

                var all = await CloudSaveService.Instance.Data.Player.LoadAllAsync();
                if (all != null)
                {
                    for (int i = 0; i < LegacyKeys.Length; i++)
                    {
                        var legacyKey = LegacyKeys[i];
                        if (string.IsNullOrEmpty(legacyKey)) continue;
                        if (!all.TryGetValue(legacyKey, out var legacyItem) || legacyItem?.Value == null) continue;

                        if (TryParseLoadout(legacyItem.Value.GetAsString(), fallback, out var migrated))
                        {
                            await SaveLoadoutAsync(migrated);
                            return migrated;
                        }
                    }
                }
            }
            catch (Unity.Services.CloudSave.CloudSaveValidationException ve)
            {
                Debug.LogWarning($"[CloudSave] Load skipped (validation): {ve.Reason} {ve.Message} {FormatValidationDetails(ve)}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudSave] Load skipped: {e.Message}");
                Debug.Log($"[CloudSave] Load stack: {e}");
            }
            return fallback;
        }

        public async Task<bool> SaveLoadoutAsync(PlayerLoadout loadout)
        {
            await EnsureReadyAsync();

            // Compact JSON string payload.
            var dto = new LoadoutDTO
            {
                primary   = loadout.Primary.ToString(),
                secondary = loadout.Secondary.ToString(),
                utility   = loadout.Utility.ToString()
            };
            var json = JsonUtility.ToJson(dto);

            // Per SDK: Dictionary<string, object> with string values.
            var payload = new Dictionary<string, object> { { Key, json } };

            try
            {
                await CloudSaveService.Instance.Data.Player.SaveAsync(payload);
                Debug.Log("[CloudSave] Loadout saved.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudSave] Save loadout failed: {e.Message}");
                return false;
            }
        }

        // DTO is a flat, serializable payload that the service accepts.
        [Serializable]
        private struct LoadoutDTO
        {
            public string primary;
            public string secondary;
            public string utility;
        }

        [Serializable]
        private struct StatsDTO
        {
            public int kills;
            public int deaths;
            public int damage;
        }

        public struct PlayerStatsRecord
        {
            public int totalKills;
            public int totalDeaths;
            public int totalDamage;
        }

        public async Task<PlayerStatsRecord> LoadStatsAsync()
        {
            await EnsureReadyAsync();

            try
            {
                var keys = new HashSet<string> { StatsKey };
                var resp = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);
                if (resp != null && resp.TryGetValue(StatsKey, out Item item))
                {
                    var json = item.Value.GetAsString();
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var dto = JsonUtility.FromJson<StatsDTO>(json);
                        return new PlayerStatsRecord
                        {
                            totalKills = dto.kills,
                            totalDeaths = dto.deaths,
                            totalDamage = dto.damage
                        };
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudSave] Load stats skipped: {e.Message}");
            }

            return default;
        }

        public async Task<bool> SaveStatsAsync(PlayerStatsRecord record)
        {
            await EnsureReadyAsync();

            var dto = new StatsDTO
            {
                kills = Mathf.Max(0, record.totalKills),
                deaths = Mathf.Max(0, record.totalDeaths),
                damage = Mathf.Max(0, record.totalDamage)
            };

            var json = JsonUtility.ToJson(dto);
            var payload = new Dictionary<string, object> { { StatsKey, json } };

            try
            {
                await CloudSaveService.Instance.Data.Player.SaveAsync(payload);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudSave] Save stats failed: {e.Message}");
                return false;
            }
        }

        public async Task<bool> AppendStatsAsync(int killsDelta, int deathsDelta, int damageDelta)
        {
            if (killsDelta <= 0 && deathsDelta <= 0 && damageDelta <= 0)
                return true; // nothing to append

            var current = await LoadStatsAsync();
            current.totalKills = SafeSum(current.totalKills, killsDelta);
            current.totalDeaths = SafeSum(current.totalDeaths, deathsDelta);
            current.totalDamage = SafeSum(current.totalDamage, damageDelta);
            return await SaveStatsAsync(current);
        }

        static int SafeSum(int baseValue, int delta)
        {
            long sum = (long)baseValue + delta;
            if (sum < 0) return 0;
            if (sum > int.MaxValue) return int.MaxValue;
            return (int)sum;
        }

        static bool TryParseLoadout(string json, PlayerLoadout fallback, out PlayerLoadout loadout)
        {
            loadout = fallback;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var dto = JsonUtility.FromJson<LoadoutDTO>(json);
                if (string.IsNullOrWhiteSpace(dto.primary) && string.IsNullOrWhiteSpace(dto.secondary) && string.IsNullOrWhiteSpace(dto.utility))
                    return false;

                var parsed = new PlayerLoadout
                {
                    Primary   = Enum.TryParse(dto.primary,   true, out PrimaryType p)   ? p : fallback.Primary,
                    Secondary = Enum.TryParse(dto.secondary, true, out SecondaryType s) ? s : fallback.Secondary,
                    Utility   = Enum.TryParse(dto.utility,   true, out UtilityType u)   ? u : fallback.Utility
                };

                if ((byte)parsed.Utility > (byte)UtilityType.Stun) parsed.Utility = UtilityType.Grenade;
                loadout = parsed;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CloudSave] Failed to parse loadout JSON: {ex.Message}");
                return false;
            }
        }

        static string FormatValidationDetails(Unity.Services.CloudSave.CloudSaveValidationException ve)
        {
            if (ve?.Details == null || ve.Details.Count == 0) return string.Empty;

            var parts = new List<string>(ve.Details.Count);
            for (int i = 0; i < ve.Details.Count; i++)
            {
                var d = ve.Details[i];
                if (d == null) continue;
                string key = !string.IsNullOrEmpty(d.Key) ? d.Key : d.Field;
                string msg = d.Messages != null && d.Messages.Count > 0 ? string.Join(";", d.Messages) : string.Empty;
                parts.Add($"{key}: {msg}");
            }
            return string.Join(" | ", parts);
        }

        // Initializes Services and ensures authentication before Cloud Save calls.
        private static async Task EnsureReadyAsync()
        {
            if (Unity.Services.Core.UnityServices.State != Unity.Services.Core.ServicesInitializationState.Initialized)
            {
                var opts = new Unity.Services.Core.InitializationOptions();
                // Keep profile short and valid for local dev.
                opts.SetProfile("Default");
                await Unity.Services.Core.UnityServices.InitializeAsync(opts);
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        public System.Collections.IEnumerator CoLoadAndSendLoadout()
        // Explicit non-generic IEnumerator to avoid CS0305 if a generic IEnumerator<T> is in scope.
        {
            // existing: fetch from UGS Cloud Save into your local loadout vars
            var fallback = new PlayerLoadout { Primary = PrimaryType.AR, Secondary = SecondaryType.Pistol, Utility = UtilityType.Grenade };

            // Avoid blocking the main thread; wait the Task in a coroutine-friendly way.
            var loadTask = LoadLoadoutAsync(fallback);
            while (!loadTask.IsCompleted) yield return null;
            var loadout = loadTask.Result;

            // Harden against "None" saved values.
            // (Cloud Save may contain explicit "None" from old clients; fix here client-side too.)
            if (loadout.Primary == PrimaryType.None) loadout.Primary = PrimaryType.AR;
            if (loadout.Secondary == SecondaryType.None) loadout.Secondary = SecondaryType.Pistol;
            if (loadout.Utility == UtilityType.None) loadout.Utility = UtilityType.Grenade;

            // Primary now defaults to AR and we sanitize any "None" returned from storage.
            // Non-blocking pattern; prevents deadlocks while keeping the coroutine flow.

            // Build small DTO for connection handshake (fits in NGO named message safely)
            var dto = new PlayerConnectionLoadoutDTO
            {
                version = 1,
                primary = (byte)loadout.Primary,
                secondary = (byte)loadout.Secondary,
                melee = 1, // Default to Knife for handshake (adjust if enum differs)
                utility = (byte)loadout.Utility
            };

// Ensures pre-join payload never carries "None" and includes Knife by default.

            // 1) Immediately inform the server (pre-spawn) via named message if connected.
            LoadoutHandshake.SendFromClient(dto);

            // 2) Keep your existing path to inform PlayerNetwork after spawn (back-compat).
            //    This ensures late joins or retries still work.
            OnLoadoutReady?.Invoke(dto);

            // existing: EquipLoadoutServerRpc(...) etc. remains as is
            // For now, just log
            Debug.Log("[CloudSave] Loadout sent to server.");
            yield return null;
        }
    }
}
