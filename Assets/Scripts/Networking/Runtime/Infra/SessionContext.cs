// Holds current session info for the running process.

using UnityEngine;

namespace Game.Net
{
    public enum ServerType { Lobby, OneVOne, TwoVTwo, None }

    public static class SessionContext
    {
        public static ServerType Type { get; private set; } = ServerType.None;
        public static string JoinCode { get; private set; } = "";
        public static string SessionId { get; private set; } = "";
        public static int MaxPlayers { get; private set; } = 0;
        public static int Threshold { get; private set; } = 0; // spawn-new threshold

        public static void Configure(ServerType type, int max, int threshold)
        {
            Type = type;
            MaxPlayers = max;
            Threshold = threshold;
        }

        public static void SetSession(string id, string code)
        {
            SessionId = id ?? "";
            JoinCode = code ?? "";
            Debug.Log($"[SessionContext] Session set. Type={Type} Id={SessionId} Code={JoinCode}");
        }
    }
}
