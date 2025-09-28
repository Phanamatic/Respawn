// Minimal IMGUI panel for Sessions with Multiplayer Services.
// Uses SessionOptionsExtensions only. No DirectNetworkOptions classes.

using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Multiplayer;

namespace Game.Net
{
    public sealed class MultiplayerQuickUI : MonoBehaviour
    {
        [Header("Session")]
        [SerializeField] private int maxPlayers = 8;
        [SerializeField] private string sessionName = "Session";

        [Header("Network")]
        [SerializeField] private NetType netType = NetType.Relay; // Relay default
        [SerializeField] private string region = null; // null -> QoS choose

        [Header("Direct (when NetType.Direct)")]
        [SerializeField] private string listenIp = "0.0.0.0";
        [SerializeField] private string publishIp = "127.0.0.1";
        [SerializeField] private int port = 7777;

        [Header("Join")]
        [SerializeField] private string joinCode = "";

        private string lastCode = "";

        private void OnGUI()
        {
            const int w = 380;
            var r = new Rect(12, 12, w, 280);
            GUI.Box(r, "Multiplayer");
            GUILayout.BeginArea(new Rect(24, 36, w - 36, 240));

            var nm = NetworkManager.Singleton;
            if (!nm)
            {
                GUILayout.Label("NetworkManager missing.");
                GUILayout.EndArea();
                return;
            }
            if (!nm.TryGetComponent<UnityTransport>(out _))
                nm.gameObject.AddComponent<UnityTransport>();

            GUILayout.Label("Session Name");
            sessionName = GUILayout.TextField(sessionName);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Max", GUILayout.Width(40));
            int.TryParse(GUILayout.TextField(maxPlayers.ToString(), GUILayout.Width(64)), out maxPlayers);
            GUILayout.EndHorizontal();
            maxPlayers = Mathf.Clamp(maxPlayers, 1, 100);

            GUILayout.Label("Network Type");
            netType = (NetType)GUILayout.SelectionGrid((int)netType, new[] { "Relay", "DA", "Direct" }, 3);

            if (netType == NetType.Direct)
            {
                GUILayout.Label("Direct Options");
                listenIp = GUILayout.TextField(listenIp);
                publishIp = GUILayout.TextField(publishIp);
                var ps = GUILayout.TextField(port.ToString(), GUILayout.Width(80));
                if (int.TryParse(ps, out var p)) port = p;
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Region", GUILayout.Width(60));
                region = GUILayout.TextField(region ?? "", GUILayout.Width(120));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host")) _ = HostAsync();
            joinCode = GUILayout.TextField(joinCode, GUILayout.Width(120));
            if (GUILayout.Button("Join")) _ = JoinAsync(joinCode);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(lastCode))
                GUILayout.Label($"Join Code: {lastCode}");

            GUILayout.Space(6);
            if (GUILayout.Button("Shutdown")) nm.Shutdown();

            GUILayout.EndArea();
        }

        private async Task HostAsync()
        {
            await UgsInitializer.EnsureAsync();

            var opts = new SessionOptions
            {
                MaxPlayers = Mathf.Clamp(maxPlayers, 1, 100),
                Name = sessionName
            };

            switch (netType)
            {
                case NetType.DA:
                    opts = opts.WithDistributedAuthorityNetwork(string.IsNullOrWhiteSpace(region) ? null : region);
                    break;
                case NetType.Direct:
                    opts = opts.WithDirectNetwork(listenIp, publishIp, port);
                    break;
                default:
                    opts = opts.WithRelayNetwork(string.IsNullOrWhiteSpace(region) ? null : region);
                    break;
            }

            var session = await MultiplayerService.Instance.CreateSessionAsync(opts);
            lastCode = session.Code;
            Debug.Log($"[MultiplayerQuickUI] Created session. Id={session.Id} Code={session.Code}");
        }

        private async Task JoinAsync(string code)
        {
            await UgsInitializer.EnsureAsync();
            await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim());
            Debug.Log($"[MultiplayerQuickUI] Joined via code {code}");
        }

        private enum NetType { Relay = 0, DA = 1, Direct = 2 }
    }
}
