using UnityEngine;

namespace Game.Net
{
    [RequireComponent(typeof(Collider))]
    public sealed class LobbyPlayTrigger : MonoBehaviour
    {
        [SerializeField] private LobbyUI lobbyUI;

        void Reset()
        {
            var col = GetComponent<Collider>();
            if (col) col.isTrigger = true;
        }

        void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag("Player")) return;
            if (!lobbyUI) lobbyUI = FindFirstObjectByType<LobbyUI>(FindObjectsInactive.Include);
            if (lobbyUI) lobbyUI.OpenPlayPanelFromWorld(transform);
        }

        void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag("Player")) return;
            if (!lobbyUI) lobbyUI = FindFirstObjectByType<LobbyUI>(FindObjectsInactive.Include);
            if (lobbyUI) lobbyUI.NotifyPlatformExited();
        }
    }
}
