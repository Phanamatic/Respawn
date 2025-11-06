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
    public static class CloudSaveClient
    {
        // Use simple, compliant key (alphanumeric only).
    const string Key = "LoadoutV1";
    const string StatsKey = "MatchStatsV1";

        public static async Task<PlayerLoadout> LoadLoadoutAsync(PlayerLoadout fallback)
        {
            try
            {
                await EnsureReadyAsync();

                var keys = new HashSet<string> { Key, "game.loadout.v1", "player.loadout" }; // backward-compat
                var resp = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);

                // pick first present key
                foreach (var k in keys)
                {
                    if (resp != null && resp.TryGetValue(k, out var item))
                    {
                        var json = item.Value.GetAsString();
                        if (string.IsNullOrWhiteSpace(json)) continue;

                        var dto = JsonUtility.FromJson<LoadoutDTO>(json);
                        var lo = new PlayerLoadout
                        {
                            Primary   = Enum.TryParse(dto.primary,   true, out PrimaryType p)   ? p : fallback.Primary,
                            Secondary = Enum.TryParse(dto.secondary, true, out SecondaryType s) ? s : fallback.Secondary,
                            Utility   = Enum.TryParse(dto.utility,   true, out UtilityType u)   ? u : fallback.Utility
                        };
                        if ((byte)lo.Utility > (byte)UtilityType.Stun) lo.Utility = UtilityType.Grenade;
                        return lo;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudSave] Load skipped: {e.Message}");
                Debug.Log($"[CloudSave] Load stack: {e}");
            }
            return fallback;
        }

        public static async Task<bool> SaveLoadoutAsync(PlayerLoadout loadout)
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

        public static async Task<PlayerStatsRecord> LoadStatsAsync()
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

        public static async Task<bool> SaveStatsAsync(PlayerStatsRecord record)
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

        public static async Task<bool> AppendStatsAsync(int killsDelta, int deathsDelta, int damageDelta)
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
    }
}
