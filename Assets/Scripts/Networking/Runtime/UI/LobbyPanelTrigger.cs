// Assets/Scripts/Networking/Runtime/UI/LobbyPanelTrigger.cs
using UnityEngine;

namespace Game.Net
{
    public enum LobbyPanel { Play, Stats, Armoury }

    [RequireComponent(typeof(Collider))]
    public sealed class LobbyPanelTrigger : MonoBehaviour
    {
        [SerializeField] private LobbyUI lobbyUI;
        [SerializeField] private LobbyPanel panel = LobbyPanel.Play;

        void Reset()
        {
            var col = GetComponent<Collider>();
            if (col) col.isTrigger = true;
        }

        void OnTriggerEnter(Collider other)
        {
            if (!IsLocalOwner(other)) return;
            if (!lobbyUI) lobbyUI = FindFirstObjectByType<LobbyUI>(FindObjectsInactive.Include);
            if (!lobbyUI) return;

            lobbyUI.OpenPanelFromWorld(panel, transform);
        }

        void OnTriggerExit(Collider other)
        {
            if (!IsLocalOwner(other)) return;
            if (!lobbyUI) lobbyUI = FindFirstObjectByType<LobbyUI>(FindObjectsInactive.Include);
            if (!lobbyUI) return;

            lobbyUI.NotifyPlatformExited(panel);
        }

        static bool IsLocalOwner(Collider other)
        {
            if (!other || !other.CompareTag("Player")) return false;
            var pn = other.GetComponentInParent<PlayerNetwork>();
            return pn && pn.IsOwner; // only the client owning this player opens UI locally
        }
    }
}
