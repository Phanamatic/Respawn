using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

namespace Game.Net.UI
{
    /// <summary>
    /// Lives across scene loads and can bounce the client to MainMenu,
    /// then trigger the existing quick-join-to-Lobby flow once.
    /// </summary>
    public sealed class ReturnToLobbyAgent : MonoBehaviour
    {
        private static bool s_AutoJoinLobbyOnce;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            // Create once
            var go = new GameObject(nameof(ReturnToLobbyAgent));
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<ReturnToLobbyAgent>();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
#if !UNITY_SERVER
            if (s_AutoJoinLobbyOnce && s.name == "MainMenu")
            {
                s_AutoJoinLobbyOnce = false;
                // Ask MainMenu to immediately start its Join flow
                Game.Net.MainMenuClientUI.SetAutoJoinLobbyOnce();
            }
#endif
        }

        /// <summary>Disconnect from match and go to MainMenu; auto-join Lobby next.</summary>
        public static void ReturnToLobbyNow(bool autoJoinLobby = true)
        {
#if !UNITY_SERVER
            s_AutoJoinLobbyOnce = autoJoinLobby;

            // Ensure any cached session/lobby/endpoints are cleared before the next quick-join.
            try { Game.Net.SessionContext.Clear(); } catch { }

            var nm = NetworkManager.Singleton;
            if (nm && nm.IsClient) nm.Shutdown();
            SceneManager.LoadScene("MainMenu");
#endif
        }


// Client-only: dedicated server SessionContext is untouched; heartbeat + lobby session remain intact.
    }
}
