using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Game.Net; // UgsInitializer

namespace Game.Bootstrap
{
    /// Ensures clients always start on the "Account" scene. Servers unaffected.
    public static class ClientBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static async void ForceAccountScene()
        {
            if (Game.Showcase.ShowcaseBootstrap.Active) return; // showcase scenes bypass auth
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return; // headless server
            const string Target = "Account";
            if (!Application.CanStreamedLevelBeLoaded(Target)) return;

            // Initialize UGS profile early for UI services usage.
            if (!UgsInitializer.IsReady)
                await UgsInitializer.EnsureAsync("production", "Default");

            var active = SceneManager.GetActiveScene().name;
            if (active != Target) SceneManager.LoadScene(Target, LoadSceneMode.Single);
        }
    }
}
// Client-only scene redirect; keeps server CLI scene flow intact.
