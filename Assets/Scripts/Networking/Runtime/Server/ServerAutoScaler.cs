// Assets/Scripts/Networking/Runtime/ServerAutoScaler.cs
// Spawns a new server process when threshold is reached.

using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
// Aliases to avoid Debug ambiguity and to reference Process types cleanly.
using UDebug = UnityEngine.Debug;
using SProcess = System.Diagnostics.Process;
using SProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Game.Net
{
    public sealed class ServerAutoScaler : NetworkBehaviour
    {
        [Tooltip("Only active on dedicated hosts.")]
        [SerializeField] private bool enable = true;

        private bool _spawnedChild;

        private void Update()
        {
            if (!enable) return;
            var nm = NetworkManager.Singleton;
            if (!nm || !nm.IsServer) return;
            if (SessionContext.Type == ServerType.None) return;
            if (_spawnedChild) return;

            int connected = nm.ConnectedClientsList.Count;
            int threshold = SessionContext.Threshold;
            if (threshold <= 0) return;

            if (connected >= threshold)
            {
                _spawnedChild = TrySpawnChildServer();
            }
        }

        private bool TrySpawnChildServer()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            var exe = SProcess.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;

            var scene = SceneManager.GetActiveScene().name;
            var type = SessionContext.Type == ServerType.Lobby ? "lobby" :
                       SessionContext.Type == ServerType.OneVOne ? "1v1" : "2v2";

            var args = $"-batchmode -nographics -mpsHost -net relay -serverType {type} -max {SessionContext.MaxPlayers} -scene \"{scene}\" -region us-west1";
            var psi = new SProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            try { SProcess.Start(psi); return true; }
            catch { return false; }
#else
            UDebug.Log("[ServerAutoScaler] Spawn skipped in Editor/non-Windows build.");
            return false;
#endif
        }
    }
}
