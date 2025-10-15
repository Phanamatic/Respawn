using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PlayFab;
using PlayFab.ClientModels;

namespace Game.UI.Common
{
    /// Attach in Main Menu. Assign Image/RawImage and TMP_Text in Inspector.
    public sealed class MainMenuPlayerHeader : MonoBehaviour
    {
        [Header("UI Targets")]
        [SerializeField] Image profileImage;         // optional
        [SerializeField] RawImage profileRawImage;   // optional
        [SerializeField] TMP_Text usernameText;      // required

        [Header("Icon Mapping (id -> sprite)")]
        [SerializeField] List<IconEntry> iconMap = new();
        [SerializeField] Sprite fallbackSprite;

        [Header("Behavior")]
        [SerializeField] bool autoRunOnStart = true;
        [SerializeField] int nameTimeoutMs = 8000;
        [SerializeField] bool setPreserveAspect = true;   // keep circle clean
        [SerializeField] bool disableIconRaycast = true;  // let parent Button receive clicks

        [Header("Debug & Fallback")]
        [SerializeField] bool logDebug = false;
        [SerializeField] bool useResourcesFallback = false;   // loads Resources/<Folder>/<iconId>.png
        [SerializeField] string resourcesFolder = "ProfileIcons";

        [System.Serializable]
        public struct IconEntry
        {
            public string id;
            public Sprite sprite;
        }

        void Start()
        {
            if (autoRunOnStart) _ = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            // 1) Name
            var name = await GetPlayerNameAsync(nameTimeoutMs);
            if (usernameText) usernameText.text = string.IsNullOrWhiteSpace(name) ? "Player" : name;

            // 2) Icon
            string iconId = await Game.Services.CloudSaveProfile.LoadProfileIconAsync(); // returns saved id or null
            if (logDebug) Debug.Log($"[MainMenuPlayerHeader] iconId='{iconId}'  map={iconMap.Count}");
            ApplyIcon(iconId);
        }

        async Task<string> GetPlayerNameAsync(int timeoutMs)
        {
            if (PlayFabClientAPI.IsClientLoggedIn())
            {
                try
                {
                    var task = GetAccountInfoAsyncCompat(new GetAccountInfoRequest());
                    var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
                    if (done == task)
                    {
                        var (result, error) = await task;
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
                catch { /* ignore, fall through */ }
            }

            // Fallback: short tail of PlayFabId, if available
            try
            {
                var pid = PlayFab.PlayFabSettings.staticPlayer?.PlayFabId;
                if (!string.IsNullOrEmpty(pid) && pid.Length >= 6) return $"Player {pid[^6..]}";
            }
            catch { }

            return null;
        }

        void ApplyIcon(string id)
        {
            Sprite s = null;

            // 1) Inspector map
            if (!string.IsNullOrEmpty(id))
                for (int i = 0; i < iconMap.Count; i++)
                    if (iconMap[i].id == id) { s = iconMap[i].sprite; break; }

            // 2) Optional Resources fallback: Resources/<resourcesFolder>/<id>
            if (!s && useResourcesFallback && !string.IsNullOrEmpty(id))
                s = Resources.Load<Sprite>(string.IsNullOrEmpty(resourcesFolder) ? id : $"{resourcesFolder}/{id}");

            // 3) Fallback sprite
            if (!s) s = fallbackSprite;

            if (profileImage)
            {
                if (s) { profileImage.sprite = s; profileImage.enabled = true; }
                // If still null, keep whatever was assigned. Do not disable.
                if (setPreserveAspect) profileImage.preserveAspect = true;
                if (disableIconRaycast) profileImage.raycastTarget = false;
            }
            if (profileRawImage)
            {
                if (s) { profileRawImage.texture = s.texture; profileRawImage.enabled = true; }
                if (disableIconRaycast) profileRawImage.raycastTarget = false;
            }
        }

        // Optional UI hook
        public void Refresh() => _ = RefreshAsync();

        // SDK-agnostic async wrapper for GetAccountInfo
        static Task<(GetAccountInfoResult result, PlayFabError error)> GetAccountInfoAsyncCompat(GetAccountInfoRequest req)
        {
            var tcs = new TaskCompletionSource<(GetAccountInfoResult, PlayFabError)>();
            PlayFabClientAPI.GetAccountInfo(
                req,
                r => tcs.TrySetResult((r, null)),
                e => tcs.TrySetResult((null, e))
            );
            return tcs.Task;
        }
    }
}