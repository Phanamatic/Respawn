using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;

namespace Game.Services
{
    /// Cloud Save helpers for player profile data (v2 APIs).
    public static class PlayerProfileStore
    {
        const string KeyProfileIcon     = "profile_icon";
        const string KeyIdentityPlayFab = "identity_playfab";
        const string KeyIdentityUgs     = "identity_ugs";

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

    /// <summary>Caches the signed-in player's display name and icon identifier across scene loads.</summary>
    public static class PlayerIdentityState
    {
        public static string LocalDisplayName { get; private set; }
        public static string LocalIconId { get; private set; }

        static bool _displayFetched;
        static bool _iconFetched;

        public static void SetLocal(string displayName, string iconId)
        {
            LocalDisplayName = Clean(displayName);
            LocalIconId = Clean(iconId);
        }

        public static void Clear()
        {
            LocalDisplayName = null;
            LocalIconId = null;
            _displayFetched = false;
            _iconFetched = false;
        }

        public static async Task EnsureIdentityAsync(int nameTimeoutMs = 8000)
        {
            var nameTask = EnsureDisplayNameAsync(nameTimeoutMs);
            var iconTask = EnsureIconIdAsync();
            await Task.WhenAll(nameTask, iconTask);
        }

        public static async Task<string> EnsureDisplayNameAsync(int timeoutMs = 8000)
        {
            if (!string.IsNullOrWhiteSpace(LocalDisplayName)) return LocalDisplayName;
            if (_displayFetched) return LocalDisplayName;
            _displayFetched = true;
            LocalDisplayName = await FetchDisplayNameAsync(timeoutMs);
            return LocalDisplayName;
        }

        public static async Task<string> EnsureIconIdAsync()
        {
            if (!string.IsNullOrWhiteSpace(LocalIconId)) return LocalIconId;
            if (_iconFetched) return LocalIconId;
            _iconFetched = true;
            try
            {
                LocalIconId = Clean(await PlayerProfileStore.LoadProfileIconAsync());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerIdentityState] Load icon failed: {e.Message}");
            }
            return LocalIconId;
        }

        static async Task<string> FetchDisplayNameAsync(int timeoutMs)
        {
            if (PlayFabClientAPI.IsClientLoggedIn())
            {
                try
                {
                    var accountTask = GetAccountInfoAsync(new GetAccountInfoRequest());
                    var finished = await Task.WhenAny(accountTask, Task.Delay(timeoutMs));
                    if (finished == accountTask)
                    {
                        var (result, error) = await accountTask;
                        if (error == null)
                        {
                            var info = result?.AccountInfo;
                            var display = info?.TitleInfo?.DisplayName;
                            if (!string.IsNullOrWhiteSpace(display)) return display;
                            var username = info?.Username;
                            if (!string.IsNullOrWhiteSpace(username)) return username;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PlayerIdentityState] Fetch name failed: {e.Message}");
                }
            }

            try
            {
                var pid = PlayFab.PlayFabSettings.staticPlayer?.PlayFabId;
                if (!string.IsNullOrEmpty(pid) && pid.Length >= 6)
                    return $"Player {pid[^6..]}";
            }
            catch { }

            return null;
        }

        static Task<(GetAccountInfoResult result, PlayFabError error)> GetAccountInfoAsync(GetAccountInfoRequest request)
        {
            var tcs = new TaskCompletionSource<(GetAccountInfoResult, PlayFabError)>();
            PlayFabClientAPI.GetAccountInfo(
                request,
                r => tcs.TrySetResult((r, null)),
                e => tcs.TrySetResult((null, e))
            );
            return tcs.Task;
        }

        static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>Runtime lookup for profile icon sprites by identifier.</summary>
    public static class ProfileIconLookup
    {
        static readonly Dictionary<string, Sprite> _icons = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        static Sprite _fallback;
        static bool _resourcesScanned;
        static string _resourcesFolder = "ProfileIcons";

        /// <summary>Registers sprites so they can be resolved later by name.</summary>
        public static void RegisterSprites(IEnumerable<Sprite> sprites, bool overwriteExisting = false)
        {
            if (sprites == null) return;
            foreach (var sprite in sprites)
            {
                if (!sprite) continue;
                if (overwriteExisting || !_icons.ContainsKey(sprite.name))
                    _icons[sprite.name] = sprite;
            }
        }

        /// <summary>Sets the fallback sprite returned when an id is missing.</summary>
        public static void SetFallback(Sprite sprite)
        {
            _fallback = sprite;
        }

        /// <summary>Sets the Resources folder scanned when resolving unknown ids.</summary>
        public static void SetResourcesFolder(string folder)
        {
            _resourcesFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
            _resourcesScanned = false;
        }

        public static Sprite Resolve(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                EnsureResourcesLoaded();
                return _fallback;
            }

            EnsureResourcesLoaded();
            if (_icons.TryGetValue(id, out var sprite))
                return sprite;
            return _fallback;
        }

        static void EnsureResourcesLoaded()
        {
            if (_resourcesScanned) return;
            _resourcesScanned = true;
            if (string.IsNullOrEmpty(_resourcesFolder)) return;

            try
            {
                var sprites = Resources.LoadAll<Sprite>(_resourcesFolder);
                if (sprites != null && sprites.Length > 0)
                    RegisterSprites(sprites);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ProfileIconLookup] Failed loading Resources/{_resourcesFolder}: {ex.Message}");
            }
        }
    }
}
// Single authoritative profile store.