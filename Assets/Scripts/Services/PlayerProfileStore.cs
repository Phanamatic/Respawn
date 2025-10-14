using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;

namespace Game.Services
{
    /// Cloud Save helpers for player profile data (v2 APIs).
    public static class PlayerProfileStore
    {
        const string KeyProfileIcon     = "profile.icon";
        const string KeyIdentityPlayFab = "identity.playfab";
        const string KeyIdentityUgs     = "identity.ugs";

        public static async Task<bool> SaveProfileIconAsync(string iconId)
        {
            try
            {
                var data = new Dictionary<string, object> { { KeyProfileIcon, iconId ?? "" } };
                await CloudSaveService.Instance.Data.Player.SaveAsync(data);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PlayerProfileStore] SaveProfileIcon failed: {e.Message}");
                return false;
            }
        }

        public static async Task<string> LoadProfileIconAsync()
        {
            try
            {
                var keys = new HashSet<string> { KeyProfileIcon };
                var res  = await CloudSaveService.Instance.Data.Player.LoadAsync(keys); // Dictionary<string, Item>
                if (res.TryGetValue(KeyProfileIcon, out Item item))
                    return item.Value.GetAsString();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PlayerProfileStore] LoadProfileIcon failed: {e.Message}");
            }
            return null;
        }

        public static async Task<bool> SaveIdentityMapAsync(string playfabId, string unityPlayerId)
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { KeyIdentityPlayFab, playfabId ?? "" },
                    { KeyIdentityUgs,     unityPlayerId ?? "" }
                };
                await CloudSaveService.Instance.Data.Player.SaveAsync(data);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PlayerProfileStore] SaveIdentityMap failed: {e.Message}");
                return false;
            }
        }
    }
}
// Single authoritative profile store.