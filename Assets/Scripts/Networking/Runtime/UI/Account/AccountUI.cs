using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;
using Game.Services;
using Game.Net;

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
        [SerializeField] string nextScene = "MainMenu";

        PlayFabAuthService _auth;
        string _selectedIconId;              // holds chosen icon before sign-in
        bool _iconSaved;                     // avoid double saves

        void Awake()
        {
            _auth = FindFirstObjectByType<PlayFabAuthService>(FindObjectsInactive.Include);
            if (_auth == null)
            {
                var go = new GameObject("PlayFabAuthService");
                _auth = go.AddComponent<PlayFabAuthService>();
            }

            showSignInButton.onClick.AddListener(() => Show(true));
            showCreateButton.onClick.AddListener(() => Show(false));

            siSubmit.onClick.AddListener(() => _ = DoSignIn());
            crSubmit.onClick.AddListener(() => _ = DoCreate());

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
            var res = await _auth.SignInAsync(email, pass);
            if (!res.ok) { siStatus.text = res.error; siSubmit.interactable = true; return; }

            siStatus.text = "OK";
            await TrySaveSelectedIconAsync(); // save if user picked before sign-in
            LoadNext();
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
            var res = await _auth.RegisterAsync(email, pass, name ?? string.Empty, user ?? string.Empty);
            if (!res.ok) { crStatus.text = res.error; crSubmit.interactable = true; return; }

            crStatus.text = "OK";
            await TrySaveSelectedIconAsync(); // persist chosen profile icon
            LoadNext();
        }

        void LoadNext()
        {
            if (!string.IsNullOrWhiteSpace(nextScene) && Application.CanStreamedLevelBeLoaded(nextScene))
                SceneManager.LoadScene(nextScene);
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
    }
}
// Minimal UI glue. Validates inputs, shows errors, loads MainMenu on success.
