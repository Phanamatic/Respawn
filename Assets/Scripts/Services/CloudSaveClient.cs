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
