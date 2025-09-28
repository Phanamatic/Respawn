// Assets/Scripts/Networking/Runtime/ServerControlListener.cs
// Graceful shutdown for headless servers via file signal: <SessionDirectory.ControlDirectory>/<SessionId>.shutdown
// Add this component to Lobby, Match_1v1, Match_2v2 server scenes.

using System.Collections;
using System.IO;
using UnityEngine;
using Unity.Netcode;

namespace Game.Net
{
    public sealed class ServerControlListener : MonoBehaviour
    {
        private string _signalPath;

        private void OnEnable() => StartCoroutine(WatchLoop());

        private IEnumerator WatchLoop()
        {
            // Wait until we are a running server with a valid SessionId
            while (NetworkManager.Singleton == null) yield return null;
            var nm = NetworkManager.Singleton;
            while (!nm.IsServer) yield return null;
            while (string.IsNullOrEmpty(SessionContext.SessionId)) yield return null;

            var dir = SessionDirectory.ControlDirectory;
            Directory.CreateDirectory(dir);
            _signalPath = Path.Combine(dir, SessionContext.SessionId + ".shutdown");

            for (;;)
            {
                if (File.Exists(_signalPath))
                {
                    TryDelete(_signalPath);
                    yield return ShutdownRoutine();
                    yield break;
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        private IEnumerator ShutdownRoutine()
        {
            Debug.Log("[ServerControl] Shutdown signal received. Quitting.");

            // Remove from local directory and stop Netcode
            if (!string.IsNullOrEmpty(SessionContext.SessionId))
                SessionDirectory.Remove(SessionContext.SessionId);

            var nm = NetworkManager.Singleton;
            if (nm && nm.IsServer) nm.Shutdown();

            yield return new WaitForSecondsRealtime(0.1f);
            Application.Quit(0);
        }
    }
}
