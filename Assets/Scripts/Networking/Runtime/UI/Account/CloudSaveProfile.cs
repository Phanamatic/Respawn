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
                if (res.TryGetValue(KeyProfileIcon, out Item item))
                    return item.Value.GetAsString();

                // Migration: fallback to old dotted key if it exists, then re-save to new key.
                var legacy = new HashSet<string> { "profile.icon" };
                var oldRes = await CloudSaveService.Instance.Data.Player.LoadAsync(legacy);
                if (oldRes.TryGetValue("profile.icon", out Item old) && old.Value != null)
                {
                    var val = old.Value.GetAsString();
                    await SaveProfileIconAsync(val);
                    return val;
                }
            }
            catch (Unity.Services.CloudSave.CloudSaveValidationException ve)
            {
                Debug.LogWarning($"[CloudSaveProfile] LoadProfileIcon validation: {ve.Reason}");
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
