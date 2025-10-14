using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using PlayFab;
using PlayFab.ClientModels;
using Unity.Services.Authentication;
using Game.Net; // UgsInitializer

namespace Game.Services
{
    /// PlayFab email/password auth + Unity Authentication external-token sign-in/link.
    public sealed class PlayFabAuthService : MonoBehaviour
    {
        public static PlayFabAuthService Instance { get; private set; }

        [Serializable] public struct SignInResult { public bool ok; public string error; public string playFabId; public string unityPlayerId; }

        void Awake()
        {
            if (Instance && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public async Task InitializeAsync()
        {
            if (!UgsInitializer.IsReady)
                await UgsInitializer.EnsureAsync("production", "Default"); // short, safe profile
        }

        public async Task<SignInResult> RegisterAsync(string email, string password, string displayName, string username)
        {
            await InitializeAsync();

            var tcs = new TaskCompletionSource<SignInResult>();
            var req = new RegisterPlayFabUserRequest
            {
                Email = email,
                Password = password,
                Username = string.IsNullOrWhiteSpace(username) ? null : username,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                RequireBothUsernameAndEmail = false
            };

            PlayFabClientAPI.RegisterPlayFabUser(req,
                async res =>
                {
                    var ticket = res.SessionTicket;
                    var link = await SignInOrLinkUgsWithPlayFabAsync(ticket);
                    tcs.TrySetResult(link);
                },
                err => tcs.TrySetResult(new SignInResult { ok = false, error = err.GenerateErrorReport() })
            );

            return await tcs.Task;
        }

        public async Task<SignInResult> SignInAsync(string email, string password)
        {
            await InitializeAsync();

            var tcs = new TaskCompletionSource<SignInResult>();
            var req = new LoginWithEmailAddressRequest { Email = email, Password = password, InfoRequestParameters = new GetPlayerCombinedInfoRequestParams { GetUserAccountInfo = true } };

            PlayFabClientAPI.LoginWithEmailAddress(req,
                async res =>
                {
                    var ticket = res.SessionTicket;
                    var link = await SignInOrLinkUgsWithPlayFabAsync(ticket);
                    tcs.TrySetResult(link);
                },
                err => tcs.TrySetResult(new SignInResult { ok = false, error = err.GenerateErrorReport() })
            );

            return await tcs.Task;
        }

        private static bool HasPlayFabIdentity()
        {
            try
            {
                var ids = AuthenticationService.Instance.PlayerInfo?.Identities;
                if (ids == null) return false;
                return ids.Any(i => string.Equals(i.TypeId, "playfab", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private static async Task<SignInResult> SignInOrLinkUgsWithPlayFabAsync(string playFabSessionTicket)
        {
            try
            {
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            catch (AuthenticationException ae)
            {
                return new SignInResult { ok = false, error = $"UGS auth failed: {ae.Message}" };
            }
            catch (System.Exception rfe)
            {
                return new SignInResult { ok = false, error = $"UGS request failed: {rfe.Message}" };
            }

            // Fetch IDs and persist mapping
            string unityId = AuthenticationService.Instance.PlayerId ?? "";
            string playfabId = PlayFabSettings.staticPlayer != null ? PlayFabSettings.staticPlayer.PlayFabId : "";

            _ = Game.Services.PlayerProfileStore.SaveIdentityMapAsync(playfabId, unityId);

            return new SignInResult { ok = true, error = null, playFabId = playfabId, unityPlayerId = unityId };
        }
    }
}
// Handles both new-account and existing-account flows with upgrade from anonymous to linked.
