// Assets/Scripts/Networking/Runtime/ServerAdvertiser.cs
// Advertises friendly names (1v1_Match_N / 2v2_Match_N) while keeping ip:port as code.

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Linq;
using UDebug = UnityEngine.Debug;
using SProcess = System.Diagnostics.Process;
using SProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Game.Net
{
    [DefaultExecutionOrder(500)]
    public sealed class ServerAdvertiser : NetworkBehaviour
    {
        private SessionDirectory.Entry _entry;

        private void OnEnable() => StartCoroutine(AdvertiseWhenReady());

        private IEnumerator AdvertiseWhenReady()
        {
            while (NetworkManager.Singleton == null) yield return null;
            var nm = NetworkManager.Singleton;

            while (!nm.IsServer) yield return null;
            while (string.IsNullOrEmpty(SessionContext.JoinCode) || string.IsNullOrEmpty(SessionContext.SessionId))
                yield return null;

            var typeKey = TypeToKey(SessionContext.Type);

            _entry = new SessionDirectory.Entry
            {
                id = SessionContext.SessionId,
                net = "relay",                                // "relay" | "direct"
                code = SessionContext.JoinCode,               // relay join code OR ip:port
                name = BuildFriendlyName(typeKey),            // human-readable name
                type = typeKey,
                max = SessionContext.MaxPlayers,
                threshold = SessionContext.Threshold,
                current = 0,
                scene = SceneManager.GetActiveScene().name,
                exe = GetExePath()
            };
#if UNITY_EDITOR
            UDebug.Log($"[ServerAdvertiser] Start advertising {_entry.type} {_entry.name} ({_entry.code})");
#endif
            yield return HeartbeatLoop(nm);
        }

        private IEnumerator HeartbeatLoop(NetworkManager nm)
        {
            while (nm && nm.IsServer)
            {
                int count = 0;
                var list = nm.ConnectedClientsIds;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != NetworkManager.ServerClientId) count++;

                _entry.current = count;
                _entry.updatedUnix = DateTimeUtils.NowUnix();
                SessionDirectory.Upsert(_entry);
                yield return new WaitForSecondsRealtime(2f);
            }
            if (!string.IsNullOrEmpty(_entry?.id)) SessionDirectory.Remove(_entry.id);
        }

        private static string TypeToKey(ServerType t) => t == ServerType.Lobby ? "lobby" : t == ServerType.OneVOne ? "1v1" : "2v2";

        private static string BuildFriendlyName(string key)
        {
            string prefix = key switch
            {
                "1v1" => "1v1_Match_",
                "2v2" => "2v2_Match_",
                _     => "Lobby_"
            };

            var used = SessionDirectory.GetSnapshot(e => e.type == key)
                ?.Select(e => e.name)
                ?.Where(n => !string.IsNullOrEmpty(n) && n.StartsWith(prefix))
                ?.Select(n =>
                {
                    var tail = n.Substring(prefix.Length);
                    return int.TryParse(tail, out var i) ? i : -1;
                })
                ?.Where(i => i > 0)
                ?.ToHashSet() ?? new System.Collections.Generic.HashSet<int>();

            int idx = 1;
            while (used.Contains(idx)) idx++;
            return prefix + idx;
        }

        private static string GetExePath()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            try { return SProcess.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return ""; }
#else
            return "";
#endif
        }
    }

    internal static class DateTimeUtils
    {
        public static long NowUnix() => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
