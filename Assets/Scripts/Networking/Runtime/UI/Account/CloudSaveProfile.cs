using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;

namespace Game.Services
{
    /// Cloud Save helpers for profile data.
    public static class CloudSaveProfile
    {
    const string KeyProfileIcon = "profile_icon"; // no dots
    const string LegacyKeyProfileIcon = "profile.icon";

        public static async Task<bool> SaveProfileIconAsync(string iconId)
        {
            try
            {
                var data = new Dictionary<string, object> { { KeyProfileIcon, iconId } };
                await CloudSaveService.Instance.Data.Player.SaveAsync(data);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CloudSaveProfile] SaveProfileIcon failed: {e.Message}");
                return false;
            }
        }

        public static async Task<string> LoadProfileIconAsync()
        {
            try
            {
                // First try new key.
                var keys = new HashSet<string> { KeyProfileIcon };
                var res = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);
                if (res != null && res.TryGetValue(KeyProfileIcon, out Item item))
                    return item.Value.GetAsString();

                // Migration: fallback to old dotted key if it exists, then re-save to new key.
                var all = await CloudSaveService.Instance.Data.Player.LoadAllAsync();
                if (all != null && all.TryGetValue(LegacyKeyProfileIcon, out Item old) && old.Value != null)
                {
                    var val = old.Value.GetAsString();
                    await SaveProfileIconAsync(val);
                    return val;
                }
            }
            catch (Unity.Services.CloudSave.CloudSaveValidationException ve)
            {
                string details = string.Empty;
                if (ve.Details != null && ve.Details.Count > 0)
                {
                    var parts = new List<string>(ve.Details.Count);
                    for (int i = 0; i < ve.Details.Count; i++)
                    {
                        var d = ve.Details[i];
                        if (d == null) continue;
                        string msg = d.Messages != null && d.Messages.Count > 0 ? string.Join(";", d.Messages) : null;
                        parts.Add($"key={d.Key ?? d.Field}: {msg}");
                    }
                    details = string.Join(" | ", parts);
                }
                Debug.LogWarning($"[CloudSaveProfile] LoadProfileIcon validation: {ve.Reason} {ve.Message} {details}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CloudSaveProfile] LoadProfileIcon failed: {e.Message}");
            }
            return null;
        }

        public static async Task<bool> SaveIdentityMapAsync(string playfabId, string unityPlayerId)
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "identity_playfab", playfabId ?? "" },
                    { "identity_ugs",     unityPlayerId ?? "" }
                };
                await CloudSaveService.Instance.Data.Player.SaveAsync(data);
                return true;
            }
            catch (Unity.Services.CloudSave.CloudSaveValidationException ve)
            {
                Debug.LogWarning($"[CloudSaveProfile] SaveIdentityMap validation: {ve.Reason}");
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CloudSaveProfile] SaveIdentityMap failed: {e.Message}");
                return false;
            }
        }
    }
}
// Uses UGS Cloud Save v3 API.
