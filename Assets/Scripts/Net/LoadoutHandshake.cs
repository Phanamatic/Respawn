using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Game.Net;

public class LoadoutHandshake : MonoBehaviour
{
    // Local DTO so we don't depend on PlayerNetwork's internal types.
    public struct PreJoinLoadout { public byte primary, secondary, melee, util; }

    private static readonly Dictionary<ulong, PreJoinLoadout> _preJoin = new();
    // Remove dependency on PlayerNetwork.NetLoadout (which isn't a nested type).

    // Cache the last local DTO if Cloud Save finishes before the client is connected.
    private static CloudSaveClient.PlayerConnectionLoadoutDTO? _pendingDto;
    bool _handlerRegistered;

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    void OnEnable()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnServerStarted += OnServerStarted;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            nm.OnClientConnectedCallback += OnClientConnected; // resend pending
            TryRegisterServerHandler();
            // Brief dev comment: Registers the server handler immediately when the component appears mid-session.
        }
        CloudSaveClient.OnLoadoutReady += OnLocalLoadoutReady; // client-side convenience hook
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
        CloudSaveClient.OnLoadoutReady -= OnLocalLoadoutReady;
        TryUnregisterServerHandler();
    }

    private void OnServerStarted()
    {
        TryRegisterServerHandler();
        // Brief dev comment: Centralises the handler wiring so registration and cleanup share one guarded path.
    }

    private void OnClientDisconnected(ulong clientId)
    {
        _preJoin.Remove(clientId);
    }

    private void OnClientConnected(ulong clientId)
    {
        // If we have a pending DTO from Cloud Save, send it now that we're connected.
        if (_pendingDto.HasValue)
        {
            SendFromClient(_pendingDto.Value);
            _pendingDto = null;
        }
    }

    // --- Client-side helpers ---

    public static void SendFromClient(CloudSaveClient.PlayerConnectionLoadoutDTO dto)
    {
        var nm = NetworkManager.Singleton;

        // If we’re not yet connected, cache and try again on connect.
        if (nm == null || !nm.IsClient || !nm.IsConnectedClient)
        {
            _pendingDto = dto;
            return;
        }

        using var writer = new FastBufferWriter(8, Allocator.Temp, 128);
        writer.WriteValueSafe(dto.version);
        writer.WriteValueSafe(dto.primary);
        writer.WriteValueSafe(dto.secondary);
        writer.WriteValueSafe(dto.melee);
        writer.WriteValueSafe(dto.utility);

        // NGO: ServerClientId is a static
        nm.CustomMessagingManager.SendNamedMessage("client_loadout", NetworkManager.ServerClientId, writer);
        _pendingDto = null; // sent successfully
    }

    private void OnLocalLoadoutReady(CloudSaveClient.PlayerConnectionLoadoutDTO dto)
    {
        // Cache the DTO in case we're not connected yet.
        _pendingDto = dto;
        // Also send via instance hook (covers cases where SendFromClient wasn't called directly)
        SendFromClient(dto);
    }

    // --- Server-side access for spawners/match controllers ---

    public static bool TryGetPreJoinLoadout(ulong clientId, out PreJoinLoadout loadout)
        => _preJoin.TryGetValue(clientId, out loadout);

    public static void Consume(ulong clientId)
    {
        _preJoin.Remove(clientId);
    }

    void TryRegisterServerHandler()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (!nm.IsServer) return;

        var cmm = nm.CustomMessagingManager;
        if (cmm == null) return;
        if (_handlerRegistered) return;

        cmm.RegisterNamedMessageHandler("client_loadout", HandleClientLoadoutMessage);
        _handlerRegistered = true;
    }

    void TryUnregisterServerHandler()
    {
        if (!_handlerRegistered) return;

        var nm = NetworkManager.Singleton;
        var cmm = nm != null ? nm.CustomMessagingManager : null;
        cmm?.UnregisterNamedMessageHandler("client_loadout");
        _handlerRegistered = false;
    }

    void HandleClientLoadoutMessage(ulong senderClientId, FastBufferReader reader)
    {
        try
        {
            byte version; reader.ReadValueSafe(out version);
            byte p, s, m, u;
            reader.ReadValueSafe(out p);
            reader.ReadValueSafe(out s);
            reader.ReadValueSafe(out m);
            reader.ReadValueSafe(out u);

            var pl = new PreJoinLoadout
            {
                primary = p,
                secondary = s,
                melee = m,
                util = u
            };

            _preJoin[senderClientId] = pl;
#if UNITY_EDITOR
            Debug.Log($"[DirectNet] Pre-join loadout received for {senderClientId}: P{p}/S{s}/M{m}/U{u}");
#endif
        }
        catch
        {
#if UNITY_EDITOR
            Debug.LogWarning($"[DirectNet] Failed to parse client_loadout from {senderClientId}");
#endif
        }
    }
}
// Server caches a tiny, authoritative loadout per client before their Player object spawns.
// Client sends via NGO named message as soon as Cloud Save completes.