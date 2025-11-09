using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PlayFab;
using PlayFab.ClientModels;
using Unity.Services.Authentication;
// Add UGS auth import for Unity PlayerId fallback.

namespace Game.UI.Common
{
    /// Attach in Main Menu. Assign Image/RawImage and TMP_Text in Inspector.
    public sealed class MainMenuPlayerHeader : MonoBehaviour
    {
        [Header("UI Targets")]
        [SerializeField] Image profileImage;         // optional
        [SerializeField] RawImage profileRawImage;   // optional
        [SerializeField] TMP_Text usernameText;      // required

        [Header("Account UI (assign in Inspector)")]
        [SerializeField] TMP_Text emailText;         // shows player's contact email (PlayFab)
        [SerializeField] TMP_Text regionText;        // shows player's region (country code)
        [SerializeField] TMP_Text accountIdText;     // shows player's account id (PlayFabId or Unity PlayerId fallback)

        [Header("Change Email UI")]
        [SerializeField] Button changeEmailButton;   // clicking triggers update
        [SerializeField] TMP_InputField changeEmailInput; // input field for new email
        [SerializeField] TMP_Text changeEmailStatus; // optional status/hint text
// New serialized references so you can wire up the texts and button in the Inspector.

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

        [SerializeField, Min(0.05f)] private float iconFadeDuration = 0.25f;

        private CanvasGroup _iconGroup;
        private Coroutine _iconFadeCo;

        [System.Serializable]
        public struct IconEntry
        {
            public string id;
            public Sprite sprite;
        }

        void Start()
        {
            if (changeEmailButton) changeEmailButton.onClick.AddListener(OnChangeEmailClicked);
            if (autoRunOnStart) _ = RefreshAsync();
        }

        void OnDestroy()
        {
            if (changeEmailButton) changeEmailButton.onClick.RemoveListener(OnChangeEmailClicked);
        }
// Hook up the Change Email button safely.

        public async Task RefreshAsync()
        {
            // Signal network activity immediately
            SetConnectingVisuals(true); // sets "Connecting…" and hides icon (alpha 0)

            // 1) Name
            var name = await GetPlayerNameAsync(nameTimeoutMs);
            if (usernameText) usernameText.text = string.IsNullOrWhiteSpace(name) ? "Player" : name;

            // 2) Icon
            string iconId = await Game.Services.CloudSaveProfile.LoadProfileIconAsync(); // returns saved id or null
            if (logDebug) Debug.Log($"[MainMenuPlayerHeader] iconId='{iconId}'  map={iconMap.Count}");
            ApplyIcon(iconId);

            // 3) Account details: email, region, id
            var emailTask  = GetPlayerEmailAsync(8000);
            var regionTask = GetPlayerRegionAsync(6000);

            string accountId = null;
            try { accountId = PlayFab.PlayFabSettings.staticPlayer?.PlayFabId; } catch { }
            if (string.IsNullOrWhiteSpace(accountId))
            {
                try { accountId = AuthenticationService.Instance?.PlayerId; } catch { /* ignore */ }
            }

            await Task.WhenAll(emailTask, regionTask);

            if (emailText)    emailText.text    = CleanUi(emailTask.Result)  ?? "—";
            if (regionText)   regionText.text   = CleanUi(regionTask.Result) ?? "—";
            if (accountIdText) accountIdText.text = CleanUi(accountId)       ?? "—";

            // Fade avatar in once everything resolved
            SetConnectingVisuals(false);
        }
// Populates email / region / account ID once resolved.
// Brief dev comment: Swap placeholder to "Connecting…" during resolve, defer icon visibility until resolve completes.

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

            // Do not force-visible here; RefreshAsync() controls the reveal timing.
            EnsureIconGroup();
            if (_iconGroup && s == null)
                _iconGroup.alpha = 0f; // keep hidden if there's no sprite
        }
// Brief dev comment: ApplyIcon no longer reveals; visibility is coordinated by RefreshAsync() for a clean fade-in.

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

        void SetConnectingVisuals(bool connecting)
        {
            if (connecting)
            {
                // Show "Connecting…" in name text, hide icon
                if (usernameText) usernameText.text = "Connecting…";
                EnsureIconGroup();
                if (_iconGroup) _iconGroup.alpha = 0f;
            }
            else
            {
                // Name is already set by RefreshAsync; fade icon in
                FadeIconIn();
            }
        }

        void EnsureIconGroup()
        {
            if (_iconGroup) return;
            // Find or add CanvasGroup to the icon's parent (assumes profileImage or profileRawImage is the icon)
            var iconObj = profileImage ? profileImage.gameObject : (profileRawImage ? profileRawImage.gameObject : null);
            if (iconObj)
            {
                _iconGroup = iconObj.GetComponent<CanvasGroup>();
                if (!_iconGroup) _iconGroup = iconObj.AddComponent<CanvasGroup>();
            }
        }

        void FadeIconIn()
        {
            EnsureIconGroup();
            if (!_iconGroup) return;
            if (_iconFadeCo != null) StopCoroutine(_iconFadeCo);
            _iconFadeCo = StartCoroutine(FadeIconInCoroutine());
        }

        System.Collections.IEnumerator FadeIconInCoroutine()
        {
            float start = _iconGroup.alpha;
            float end = 1f;
            float time = 0f;
            while (time < iconFadeDuration)
            {
                time += Time.deltaTime;
                _iconGroup.alpha = Mathf.Lerp(start, end, time / iconFadeDuration);
                yield return null;
            }
            _iconGroup.alpha = end;
            _iconFadeCo = null;
        }

        // ===== New helpers (email/region + change email) =====
        async Task<string> GetPlayerEmailAsync(int timeoutMs = 8000)
        {
            // Uses PlayFab GetAccountInfo -> PrivateInfo.Email (player-visible)
            try
            {
                var task = GetAccountInfoAsyncCompat(new GetAccountInfoRequest());
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
                if (done == task)
                {
                    var (res, err) = await task;
                    if (err == null)
                    {
                        var email = res?.AccountInfo?.PrivateInfo?.Email;
                        if (!string.IsNullOrWhiteSpace(email)) return email;
                    }
                }
            }
            catch { /* ignore */ }
            return null;
        }

        async Task<string> GetPlayerRegionAsync(int timeoutMs = 6000)
        {
            // Uses PlayFab GetPlayerProfile -> Locations.CountryCode
            try
            {
                var req = new GetPlayerProfileRequest
                {
                    PlayFabId = PlayFab.PlayFabSettings.staticPlayer?.PlayFabId,
                    ProfileConstraints = new PlayerProfileViewConstraints { ShowLocations = true }
                };
                var task = GetPlayerProfileAsyncCompat(req);
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
                if (done == task)
                {
                    var (res, err) = await task;
                    if (err == null)
                    {
                        var locs = res?.PlayerProfile?.Locations;
                        if (locs != null && locs.Count > 0)
                        {
                            // Prefer the first entry; PlayFab does not guarantee order
                            var cc = locs[0].CountryCode;
                            if (cc != null) return cc.ToString();
                        }
                    }
                }
            }
            catch { /* ignore */ }
            return null;
        }

        static Task<(GetPlayerProfileResult result, PlayFabError error)> GetPlayerProfileAsyncCompat(GetPlayerProfileRequest req)
        {
            var tcs = new TaskCompletionSource<(GetPlayerProfileResult, PlayFabError)>();
            PlayFabClientAPI.GetPlayerProfile(
                req,
                r => tcs.TrySetResult((r, null)),
                e => tcs.TrySetResult((null, e))
            );
            return tcs.Task;
        }

        void OnChangeEmailClicked()
        {
            _ = ChangeEmailAsync();
        }

        async Task ChangeEmailAsync()
        {
            string newEmail = changeEmailInput ? changeEmailInput.text : null;
            if (string.IsNullOrWhiteSpace(newEmail) || !newEmail.Contains("@"))
            {
                SetEmailStatus("Enter a valid email address.");
                return;
            }

            SetEmailUiBusy(true);

            string error = null;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var req = new AddOrUpdateContactEmailRequest { EmailAddress = newEmail };

            PlayFabClientAPI.AddOrUpdateContactEmail(
                req,
                _ => tcs.TrySetResult(true),
                e => { error = e.GenerateErrorReport(); tcs.TrySetResult(false); }
            );

            var ok = await tcs.Task;
            SetEmailUiBusy(false);

            if (ok)
            {
                if (emailText) emailText.text = newEmail;
                SetEmailStatus("Email updated. Check your inbox to verify.");
            }
            else
            {
                SetEmailStatus($"Update failed: {error}");
            }
        }

        void SetEmailUiBusy(bool busy)
        {
            if (changeEmailButton) changeEmailButton.interactable = !busy;
            if (changeEmailInput)  changeEmailInput.interactable  = !busy;
        }

        void SetEmailStatus(string msg)
        {
            if (changeEmailStatus) changeEmailStatus.text = msg;
        }

        static string CleanUi(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        // Helpers for account info and change-email actions.
    }
}