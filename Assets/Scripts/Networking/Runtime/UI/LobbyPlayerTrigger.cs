using UnityEngine;

namespace Game.Net
{
    [RequireComponent(typeof(Collider))]
    public sealed class LobbyPlayTrigger : MonoBehaviour
    {
        [SerializeField] private LobbyUI lobbyUI;
        [SerializeField, Min(0f)] private float spawnEntryGraceSeconds = 0.6f;

        void Reset()
        {
            var col = GetComponent<Collider>();
            if (col) col.isTrigger = true;
        }

        void OnTriggerEnter(Collider other)
        {
            var player = ResolveLocalPlayer(other);
            if (!player) return;

            if (Time.time - player.LocalSpawnTime < spawnEntryGraceSeconds)
                return;

            if (!lobbyUI) lobbyUI = FindFirstObjectByType<LobbyUI>(FindObjectsInactive.Include);
            if (lobbyUI) lobbyUI.OpenPlayPanelFromWorld(transform);
        }

        void OnTriggerExit(Collider other)
        {
            var player = ResolveLocalPlayer(other);
            if (!player) return;

            if (!lobbyUI) lobbyUI = FindFirstObjectByType<LobbyUI>(FindObjectsInactive.Include);
            if (lobbyUI) lobbyUI.NotifyPlatformExited();
        }

        PlayerNetwork ResolveLocalPlayer(Collider other)
        {
            if (!other || !other.CompareTag("Player")) return null;

            var player = other.GetComponentInParent<PlayerNetwork>();
            if (!player || !player.IsOwner) return null;

            return player;
        }
    }
}
