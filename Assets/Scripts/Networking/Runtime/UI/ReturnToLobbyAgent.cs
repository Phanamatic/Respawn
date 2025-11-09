using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

namespace Game.Net.UI
{
    /// <summary>
    /// Safe return flow: request disconnect, wait for OnClientDisconnect, THEN load MainMenu.
    /// Also triggers one-shot auto-join to Lobby via MainMenuClientUI.
    /// Includes auto-recovery if server disconnects us while in a Match scene.
    /// </summary>
    [DefaultExecutionOrder(-9999)]
    public sealed class ReturnToLobbyAgent : MonoBehaviour
    {
        private static bool s_AutoJoinLobbyOnce;
        private static ReturnToLobbyAgent s_Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (s_Instance) return;
            var go = new GameObject(nameof(ReturnToLobbyAgent));
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            s_Instance = go.AddComponent<ReturnToLobbyAgent>();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnEnable()
        {
            var nm = NetworkManager.Singleton;
            if (nm) nm.OnClientDisconnectCallback += OnClientDisconnected;
        }

        private void OnDisable()
        {
            var nm = NetworkManager.Singleton;
            if (nm) nm.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private static void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
#if !UNITY_SERVER
            if (s_AutoJoinLobbyOnce && s.name == "MainMenu")
            {
                s_AutoJoinLobbyOnce = false;
                Game.Net.MainMenuClientUI.SetAutoJoinLobbyOnce();
            }
#endif
        }

        /// <summary>
        /// Public entry: cleanly leave the match and bounce to MainMenu; optionally auto-join a Lobby once there.
        /// </summary>
        public static void ReturnToLobbyNow(bool autoJoinLobby = true)
        {
#if !UNITY_SERVER
            s_AutoJoinLobbyOnce = autoJoinLobby;

            var nm = NetworkManager.Singleton;
            if (nm)
            {
                // Ensure single subscription
                nm.OnClientDisconnectCallback -= OnClientDisconnected;
                nm.OnClientDisconnectCallback += OnClientDisconnected;

                // If we're connected, shut down the client and WAIT for the disconnect callback.
                if (nm.IsClient && nm.IsConnectedClient)
                {
                    nm.Shutdown();
                    return; // scene load will happen in callback
                }
            }

            // Already offline: just go.
            SceneManager.LoadScene("MainMenu");
#endif
        }

        private static void OnClientDisconnected(ulong clientId)
        {
#if !UNITY_SERVER
            var nm = NetworkManager.Singleton;
            if (nm)
            {
                // Only react to our own local disconnect
                if (clientId != nm.LocalClientId) return;
                nm.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            // Check if we're in a match scene - if so, auto-recover to lobby
            // This catches the "server kicked everyone" case
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var inMatch = FindFirstObjectByType<Match1v1Controller>(FindObjectsInactive.Exclude) != null;
#else
            var inMatch = FindObjectOfType<Match1v1Controller>(false) != null;
#endif
            if (inMatch)
            {
                // Server kicked us during match - set auto-join flag
                s_AutoJoinLobbyOnce = true;
            }

            SceneManager.LoadScene("MainMenu");
#endif
        }
    }
}
