using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;
using System;
using System.Threading;
using Game.Services;
using Game.Net;

// + dev quick-join deps
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
// Reuse UgsInitializer from NetBootstrap (DontDestroy runner)
using System.Net;
using System.Net.Sockets;
// IPv4 pre-resolve for direct UTP connect.

namespace Game.UI.Account
{
    /// Simple two-panel switcher and robust auth calls with validation and feedback.
    public sealed class AccountUI : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] GameObject signInPanel;
        [SerializeField] GameObject createPanel;

        [Header("Switch Buttons")]
        [SerializeField] Button showSignInButton;
        [SerializeField] Button showCreateButton;

        [Header("Sign In")]
        [SerializeField] TMP_InputField siEmail;
        [SerializeField] TMP_InputField siPassword;
        [SerializeField] Button siSubmit;
        [SerializeField] TMP_Text siStatus;
        [SerializeField] Button siToCreateButton;   // local switch

        [Header("Create")]
        [SerializeField] TMP_InputField crEmail;
        [SerializeField] TMP_InputField crUsername;          // new
        [SerializeField] TMP_InputField crPassword;
        [SerializeField] TMP_InputField crPasswordConfirm;   // new
        [SerializeField] TMP_InputField crDisplayName;
        [SerializeField] Button crSubmit;
        [SerializeField] TMP_Text crStatus;
        [SerializeField] Button crToSignInButton;   // local switch

        [Header("Profile Icons")]
        [SerializeField] IconCarousel iconCarousel;          // ScrollRect + Content driver
        [SerializeField] Transform iconsContent;             // legacy (not used by logic anymore)
        [SerializeField] ProfileIconItem iconItemPrefab;     // prefab with Button+Image
        [SerializeField] Sprite[] availableIcons;            // assign in Inspector
        [SerializeField] Image selectedIconPreview;          // optional preview


        [Header("On Success")]
        [SerializeField] string nextScene = "Loading Screen";

        [Header("Dev Quick Test")]
        [SerializeField] Button devSignIn1Button;
        [SerializeField] Button devSignIn2Button;
        [SerializeField] string dev1Email;
        [SerializeField] string dev1Password;
        [SerializeField] string dev2Email;
        [SerializeField] string dev2Password;
        // Two scene-wired buttons that use preset creds for fast test login + 1v1 quick-join.

        PlayFabAuthService _auth;
        const int AuthTimeoutMs = 15000;     // 15s guard for auth calls
        string _selectedIconId;              // holds chosen icon before sign-in
        bool _iconSaved;                     // avoid double saves
        bool _devJoinInFlight;               // prevent overlapping quick-join attempts

        void Awake()
        {
            _auth = FindFirstObjectByType<PlayFabAuthService>(FindObjectsInactive.Include);
            if (_auth == null)
            {
                var go = new GameObject("PlayFabAuthService");
                _auth = go.AddComponent<PlayFabAuthService>();
            }

            Game.Services.ProfileIconLookup.SetResourcesFolder("ProfileIcons");
            if (availableIcons != null && availableIcons.Length > 0)
            {
                Game.Services.ProfileIconLookup.RegisterSprites(availableIcons);
                Sprite fallback = availableIcons[0];
                if (selectedIconPreview && selectedIconPreview.sprite)
                    fallback = selectedIconPreview.sprite;
                Game.Services.ProfileIconLookup.SetFallback(fallback);
            }

            showSignInButton.onClick.AddListener(() => Show(true));
            showCreateButton.onClick.AddListener(() => Show(false));

            siSubmit.onClick.AddListener(() => _ = DoSignIn());
            crSubmit.onClick.AddListener(() => _ = DoCreate());

            // dev quick test buttons
            if (devSignIn1Button) devSignIn1Button.onClick.AddListener(() => _ = DevLoginAndJoin(dev1Email, dev1Password));
            if (devSignIn2Button) devSignIn2Button.onClick.AddListener(() => _ = DevLoginAndJoin(dev2Email, dev2Password));

            // per-panel switchers
            if (siToCreateButton) siToCreateButton.onClick.AddListener(() => Show(false));
            if (crToSignInButton) crToSignInButton.onClick.AddListener(() => Show(true));

            BuildIconList(); // populate profile icons
            Show(true); // default to Sign In
        }

        void Show(bool signIn)
        {
            signInPanel.SetActive(signIn);
            createPanel.SetActive(!signIn);
            if (siStatus) siStatus.text = "";
            if (crStatus) crStatus.text = "";
        }

        static bool ValidEmail(string s) => !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        static bool ValidPassword(string s) => !string.IsNullOrEmpty(s) && s.Length >= 6;

        async Task DoSignIn()
        {
            var email = siEmail.text.Trim();
            var pass  = siPassword.text;
            if (!ValidEmail(email)) { siStatus.text = "Invalid email."; return; }
            if (!ValidPassword(pass)) { siStatus.text = "Password too short."; return; }

            siSubmit.interactable = false; siStatus.text = "Signing in...";
            try
            {
                var res = await TimeoutAfter(_auth.SignInAsync(email, pass), AuthTimeoutMs);
                if (!res.ok) { siStatus.text = res.error ?? "Sign-in failed."; return; }

                siStatus.text = "OK";
                await TrySaveSelectedIconAsync();
                // await CacheIdentityAsync(_selectedIconId);
                LoadNext();
            }
            catch (Exception e)
            {
                siStatus.text = $"Sign-in failed: {ShortReason(e)}";
            }
            finally
            {
                siSubmit.interactable = true;
            }
        }

        async Task DoCreate()
        {
            var email = crEmail.text.Trim();
            var user  = crUsername ? crUsername.text.Trim() : null;
            var pass  = crPassword.text;
            var pass2 = crPasswordConfirm ? crPasswordConfirm.text : null;
            var name  = crDisplayName ? crDisplayName.text.Trim() : null;

            if (!ValidEmail(email)) { crStatus.text = "Invalid email."; return; }
            if (!ValidPassword(pass)) { crStatus.text = "Password too short."; return; }
            if (!string.IsNullOrEmpty(pass2) && pass2 != pass) { crStatus.text = "Passwords do not match."; return; }
            if (!string.IsNullOrEmpty(name) && name.Length < 2) { crStatus.text = "Name too short."; return; }

            crSubmit.interactable = false; crStatus.text = "Creating...";
            try
            {
                var res = await TimeoutAfter(_auth.RegisterAsync(email, pass, name ?? string.Empty, user ?? string.Empty), AuthTimeoutMs);
                if (!res.ok) { crStatus.text = res.error ?? "Create failed."; return; }

                crStatus.text = "OK";
                await TrySaveSelectedIconAsync();

                // Ensure a default loadout is saved for brand-new accounts.
                // Idempotent: safe to write even if a loadout already exists.
                try
                {
                    if (CloudSaveClient.Instance != null)
                        await CloudSaveClient.Instance.SaveLoadoutAsync(PlayerLoadout.Default);
                    else
                        Debug.LogWarning("[AccountUI] CloudSaveClient.Instance missing; default loadout not written.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[AccountUI] Default loadout save failed: {e.Message}");
                }

                // await CacheIdentityAsync(_selectedIconId);
                LoadNext();
            }
            catch (Exception e)
            {
                crStatus.text = $"Create failed: {ShortReason(e)}";
            }
            finally
            {
                crSubmit.interactable = true;
            }
        }

        void LoadNext()
        {
            if (!string.IsNullOrWhiteSpace(nextScene) && Application.CanStreamedLevelBeLoaded(nextScene))
                SceneManager.LoadScene(nextScene);
        }

        // Shared helpers
        static async Task<T> TimeoutAfter<T>(Task<T> task, int ms)
        {
            using var cts = new CancellationTokenSource();
            var delay = Task.Delay(ms, cts.Token);
            var done = await Task.WhenAny(task, delay);
            if (done == task) { cts.Cancel(); return await task; }
            throw new TimeoutException("Network timeout");
        }

        static string ShortReason(Exception e)
        {
            return e is TimeoutException ? "Timed out" : (e?.Message ?? "Unknown error");
        }

        void BuildIconList()
        {
            if (!iconCarousel || !iconItemPrefab || availableIcons == null) return;
            iconCarousel.Rebuild(availableIcons, iconItemPrefab, OnIconPicked);
        }

        void OnIconPicked(string id, Sprite spr)
        {
            _selectedIconId = id;
            if (selectedIconPreview && spr) selectedIconPreview.sprite = spr;
            _iconSaved = false;

            if (iconCarousel)
            {
                iconCarousel.SetSelected(id); // highlight with border frame
                iconCarousel.CenterOn(id);    // center after pick
            }
        }

        async Task TrySaveSelectedIconAsync()
        {
            if (string.IsNullOrEmpty(_selectedIconId) || _iconSaved) return;
            var ok = await PlayerProfileStore.SaveProfileIconAsync(_selectedIconId);
            _iconSaved = ok;
        }

        // async Task CacheIdentityAsync(string iconOverride = null)
        // {
        //     string displayName = await Game.Services.PlayerIdentityState.EnsureDisplayNameAsync();
        //     string iconId = string.IsNullOrWhiteSpace(iconOverride)
        //         ? await Game.Services.PlayerIdentityState.EnsureIconIdAsync()
        //         : iconOverride.Trim();
        //     Game.Services.PlayerIdentityState.SetLocal(displayName, iconId);
        // }


        // ---------- Dev quick test: sign in then join first open 1v1 server ----------
        async Task DevLoginAndJoin(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            { if (siStatus) siStatus.text = "Dev creds not set."; return; }

            // UI guard
            if (devSignIn1Button) devSignIn1Button.interactable = false;
            if (devSignIn2Button) devSignIn2Button.interactable = false;

            try
            {
                if (siStatus) siStatus.text = "Dev sign-in...";
                var res = await TimeoutAfter(_auth.SignInAsync(email.Trim(), password), AuthTimeoutMs);
                if (!res.ok) { if (siStatus) siStatus.text = res.error ?? "Sign-in failed."; return; }

                if (siStatus) siStatus.text = "Signed in.";

                // Save chosen icon if any, but do NOT switch scenes
                await TrySaveSelectedIconAsync();
                // await CacheIdentityAsync(_selectedIconId);

                // Ensure UGS is ready for Lobby queries
                if (siStatus) siStatus.text = "UGS init...";
                while (!Game.Net.UgsInitializer.IsReady)
                {
                    if (!string.IsNullOrEmpty(Game.Net.UgsInitializer.LastError))
                    { if (siStatus) siStatus.text = "UGS init failed."; return; }
                    await Task.Yield();
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                { if (siStatus) siStatus.text = "UGS not signed in."; return; }

                // Join first available 1v1 via public endpoint
                StartCoroutine(CoJoinFirstAvailable1v1());
            }
            catch (Exception e)
            {
                if (siStatus) siStatus.text = $"Dev sign-in failed: {ShortReason(e)}";
            }
            finally
            {
                if (devSignIn1Button) devSignIn1Button.interactable = true;
                if (devSignIn2Button) devSignIn2Button.interactable = true;
            }
        }

        IEnumerator CoJoinFirstAvailable1v1()
        {
            if (_devJoinInFlight) yield break;
            _devJoinInFlight = true;
            try
            {
                if (siStatus) siStatus.text = "Finding 1v1...";
                // Query: available > 0 and S1 == "OneVOne"
            var queryOptions = new QueryLobbiesOptions
            {
                Count = 25,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, "OneVOne", QueryFilter.OpOptions.EQ)
                },
                Order = new List<QueryOrder> { new QueryOrder(false, QueryOrder.FieldOptions.AvailableSlots) }
            };
            var task = LobbyService.Instance.QueryLobbiesAsync(queryOptions);
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.Exception != null)
            { if (siStatus) siStatus.text = "Lobby query failed."; yield break; }

            var matches = task.Result.Results;
            if (matches == null || matches.Count == 0)
            { if (siStatus) siStatus.text = "No 1v1 open."; yield break; }

            var best = matches[0];
            if (siStatus) siStatus.text = $"Joining {best.Name}...";

            var nm = NetworkManager.Singleton;
            if (!nm) { if (siStatus) siStatus.text = "No NetworkManager."; yield break; }
            var utp = nm.GetComponent<UnityTransport>();

            if (nm.IsConnectedClient)
            {
                nm.Shutdown();
                while (nm.IsConnectedClient) yield return null;
            }

            if (best.Data == null)
            { if (siStatus) siStatus.text = "Missing endpoint data."; yield break; }

            bool hasHost = best.Data.TryGetValue("PublicHost", out var publicHostData);
            bool hasPort = best.Data.TryGetValue("PublicPort", out var publicPortData);
            string lanEp = best.Data.TryGetValue("LanEndpoint", out var lanEpData) ? lanEpData.Value : null;

            if (!hasHost || !hasPort || string.IsNullOrWhiteSpace(publicHostData.Value))
            { if (siStatus) siStatus.text = "Invalid endpoint."; yield break; }

            string publicHost = publicHostData.Value;
            int publicPort = int.TryParse(publicPortData.Value, out var pp) ? pp : 7777;

            IEnumerator TryConnect(string host, int prt, float seconds)
            {
                // Pre-resolve to IPv4. UTP is IPv4-only in this build.
                string target = host;
                if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    try
                    {
                        var addrs = Dns.GetHostAddresses(host);
                        foreach (var a in addrs) { if (a.AddressFamily == AddressFamily.InterNetwork) { target = a.ToString(); break; } }
                    } catch { /* fall back to original host */ }
                }

                if (nm.IsServer || nm.IsHost || nm.IsClient || nm.IsListening) yield break;

                utp.SetConnectionData(target, (ushort)prt);
                if (!nm.StartClient()) yield break;

                float t = seconds;
                while (nm.IsClient && !nm.IsConnectedClient && t > 0f)
                {
                    t -= Time.deltaTime; yield return null;
                }
            }

            // Prefer LAN if provided
            bool connected = false;
            if (!string.IsNullOrWhiteSpace(lanEp) && lanEp.Contains(":"))
            {
                var parts = lanEp.Split(':');
                var lh = parts[0].Trim();
                var lp = (parts.Length > 1 && int.TryParse(parts[1], out var v)) ? v : publicPort;

                if (lh != "0.0.0.0" && lh != "127.0.0.1")
                {
                    if (siStatus) siStatus.text = $"Connecting (LAN) {lh}:{lp}";
                    yield return StartCoroutine(TryConnect(lh, lp, 5f));
                    connected = nm.IsConnectedClient;
                    if (!connected)
                    {
                        nm.Shutdown();
                        yield return new WaitUntil(() => !nm.IsClient && !nm.IsServer && !nm.IsHost && !nm.IsListening);
                    }
                }
            }

            if (!connected)
            {
                if (siStatus) siStatus.text = $"Connecting {publicHost}:{publicPort}";
                yield return StartCoroutine(TryConnect(publicHost, publicPort, 10f));
            }

            if (nm.IsConnectedClient)
            {
                if (siStatus) siStatus.text = "Connected.";
                SessionContext.SetSession(best.Id, "");
                SessionContext.SetLobby(best);
                SessionContext.SetDirectEndpoint(publicHost, publicPort, lanEp);
                // Server scene management will pull us into the match.
            }
            else
            {
                if (siStatus) siStatus.text = "Connect failed.";
                nm.Shutdown();
                yield return new WaitUntil(() => !nm.IsClient && !nm.IsServer && !nm.IsHost && !nm.IsListening);
            }
            // --- DEV NOTE ---
            _devJoinInFlight = false; // handled by try/finally below
        }
        finally { _devJoinInFlight = false; }
        }
    }
}
// Minimal UI glue. Validates inputs, shows errors, loads MainMenu on success.
