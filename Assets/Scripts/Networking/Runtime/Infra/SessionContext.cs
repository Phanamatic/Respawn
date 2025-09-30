// Assets/Scripts/Networking/Runtime/SessionContext.cs
// Updated to hold Unity Lobby reference for relay-based sessions

using UnityEngine;
using Unity.Services.Lobbies.Models;

namespace Game.Net
{
    public enum ServerType { Lobby, OneVOne, TwoVTwo, None }

    public static class SessionContext
    {
        public static ServerType Type { get; private set; } = ServerType.None;
        public static string JoinCode { get; private set; } = "";
        public static string SessionId { get; private set; } = "";
        public static int MaxPlayers { get; private set; } = 0;
        public static int Threshold { get; private set; } = 0;
        
        // Unity Lobby reference for relay sessions
        public static Lobby CurrentLobby { get; private set; } = null;

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

        public static void SetLobby(Lobby lobby)
        {
            CurrentLobby = lobby;
            if (lobby != null)
            {
                Debug.Log($"[SessionContext] Unity Lobby set. Name={lobby.Name} Id={lobby.Id}");
            }
        }

        public static void Clear()
        {
            Type = ServerType.None;
            JoinCode = "";
            SessionId = "";
            MaxPlayers = 0;
            Threshold = 0;
            CurrentLobby = null;
        }
    }
}