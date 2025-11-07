using UnityEngine;
using UnityEngine.UI;

namespace UI.Scripts.Universal
{
    /// <summary>
    /// Universal mute button that mutes all audio in any scene.
    /// Preserves slider values and only silences/unsilences the audio.
    /// </summary>
    public class UniversalMuteToggle : MonoBehaviour
    {
        [Header("Button Reference")]
        [SerializeField] private Button muteButton;

        [Header("Image Swap")]
        [SerializeField] private Image buttonImage;
        [SerializeField] private Sprite mutedSprite;
        [SerializeField] private Sprite unmutedSprite;

        [Header("Settings")]
        [SerializeField] private bool startMuted = false;

        private const string MUTE_STATE_KEY = "IsMuted";
        private const string SAVED_MASTER_VOLUME_KEY = "SavedMasterVolumeBeforeMute";

        private bool isMuted;

        private void Start()
        {
            // Load saved mute state
            isMuted = PlayerPrefs.GetInt(MUTE_STATE_KEY, startMuted ? 1 : 0) == 1;

            // Setup button listener
            if (muteButton != null)
            {
                muteButton.onClick.AddListener(ToggleMute);
            }

            // Apply initial state
            ApplyMuteState(false); // Don't animate on startup
            UpdateButtonImage();
        }

        public void ToggleMute()
        {
            isMuted = !isMuted;
            ApplyMuteState(true); // Animate when user toggles
            UpdateButtonImage();
            SaveMuteState();
        }

        private void ApplyMuteState(bool animate)
        {
            if (isMuted)
            {
                MuteAllAudio();
            }
            else
            {
                UnmuteAllAudio();
            }
        }

        private void MuteAllAudio()
        {
            // Save the current master volume before muting
            float currentMasterVolume = AudioListener.volume;
            PlayerPrefs.SetFloat(SAVED_MASTER_VOLUME_KEY, currentMasterVolume);
            PlayerPrefs.Save();

            // Mute by setting AudioListener volume to 0
            AudioListener.volume = 0f;
        }

        private void UnmuteAllAudio()
        {
            // Restore the saved master volume
            float savedVolume = PlayerPrefs.GetFloat(SAVED_MASTER_VOLUME_KEY, 1f);
            AudioListener.volume = savedVolume;
        }

        private void UpdateButtonImage()
        {
            if (buttonImage != null)
            {
                buttonImage.sprite = isMuted ? mutedSprite : unmutedSprite;
            }
        }

        private void SaveMuteState()
        {
            PlayerPrefs.SetInt(MUTE_STATE_KEY, isMuted ? 1 : 0);
            PlayerPrefs.Save();
        }

        private void OnDestroy()
        {
            if (muteButton != null)
            {
                muteButton.onClick.RemoveListener(ToggleMute);
            }
        }

        // Public API for external scripts
        public bool IsMuted => isMuted;

        public void SetMuted(bool muted)
        {
            if (isMuted != muted)
            {
                ToggleMute();
            }
        }
    }
}
