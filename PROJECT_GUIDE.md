# PROJECT_GUIDE.md

This file provides guidance for developers when working with code in this repository.

## Project Overview

This is a Unity multiplayer FPS game called "Respawn" built with Unity Netcode for GameObjects and Unity Gaming Services. The project implements a client-server architecture with dedicated servers, supporting lobby, 1v1, and 2v2 match types.

## Architecture

### Networking Architecture
The project uses Unity Netcode for GameObjects with a custom bootstrapping system:

- **NetBootstrap.cs**: Main networking initialization that handles command-line arguments, Unity Gaming Services setup, and network manager configuration
- **SessionContext.cs**: Central configuration store for server types (Lobby/1v1/2v2), player limits, and session information
- **ServerAdvertiser.cs**: Handles server discovery via a session directory system with heartbeat updates
- **SessionDirectory.cs**: File-based server registry for local development and testing

### Key Components
- **Client-Server Architecture**: Dedicated server builds with separate client builds
- **Direct Connection**: Uses IP:port for server connections (simplified from relay)
- **Multi-Scene Support**: MainMenu → Lobby → Match flow
- **Server Types**: Lobby (16 players), 1v1 (2 players), 2v2 (4 players)

### Build System
The project includes a comprehensive build system via `QuickBuildAndRun.cs`:

- **Build Profiles**: Separate client/server/admin build configurations
- **Automated Builds**: Unity Editor menu items for quick building
- **Server Management**: Start/stop/monitor dedicated servers
- **Log Monitoring**: PowerShell-based log tailing for server debugging

## Common Development Commands

### Building
Use Unity Editor menu items under "Build/Quick/":
- `Build Server (Dedicated)` - Creates dedicated server build
- `Build Client` - Creates client build
- `Build Admin` - Creates admin/monitoring build
- `Build Client+Server` - Builds both client and server
- `Build All` - Builds all variants

### Running Servers
Use Unity Editor menu items under "Build/Quick/Run Seeds/":
- `Run Lobby` - Starts lobby server on port 7777
- `Run 1v1` - Starts 1v1 match server on port 7778
- `Run 2v2` - Starts 2v2 match server on port 7779
- `Run All` - Starts all server types

### Server Management
- `Kill All Server Processes` - Terminates all running server instances
- Log monitoring available via "Build/Quick/Logs/" menu items

### Manual Server Launch
Server builds accept command-line arguments:
```
Server.exe -batchmode -nographics -mpsHost -serverType lobby -max 16 -scene Lobby -env production -logfile .\lobby.log -net direct -port 7777
```

### Client Connection
Clients can auto-connect via command line:
```
Client.exe -mpsJoin 127.0.0.1:7777 -autoJoin
```

## Project Structure

### Key Directories
- `Assets/Scripts/Networking/Runtime/` - Core networking code
- `Assets/Scenes/Game/` - Client game scenes (MainMenu, Lobby, Match_1v1, Match_2v2)
- `Assets/Scenes/Server/` - Server scenes (Bootstrap, Admin)
- `Assets/Editor/Build/` - Build automation scripts
- `Assets/Settings/Build Profiles/` - Unity build profile configurations

### Important Files
- `NetBootstrap.cs` - Network initialization and command-line parsing
- `SessionContext.cs` - Session configuration management
- `ServerAdvertiser.cs` - Server discovery and heartbeat
- `QuickBuildAndRun.cs` - Build automation and server management
- `PlayerAvatar.cs` / `PlayerNetwork.cs` - Player networking components

## Unity Gaming Services Integration

The project integrates with Unity Gaming Services (UGS):
- **Authentication**: Anonymous sign-in for all clients
- **Environment Support**: Configurable environments (production/development)
- **Profile Management**: Editor vs client profile separation

## Development Notes

### Spectator mode scene setup
1. **Project defaults**: Connection approval is enabled automatically by `ConnectionMetadataInstaller` at runtime so no prefab changes are needed. Ensure `NetworkManager` exists in each scene (Bootstrap/Lobby/Match) so the installer can find and configure it.
2. **Lobby UI wiring (Lobby scene)**:
   - Select the `LobbyUI` component on the lobby canvas and assign:
     - **Spectate Button** → the button visible only for accounts named `Spectator1` or `Spectator2`.
     - **Spectate Panel** → panel container that opens when clicking Spectate.
     - **Spectate Close Button** → close/back control on the panel.
     - **Spectate List Root** → layout group under the panel where entries are spawned.
     - **Spectate Entry Prefab** → UI prefab with `TMP_Text` fields for name/mode/phase/stats and a "Join as Spectator" `Button` (the prefab must have a `RectTransform`).
     - **Spectate Status Text** → text element for connection feedback/errors.
   - Confirm existing play/armoury/stats fields remain assigned so spectator wiring does not break existing flow.
3. **Match scene camera (Match_1v1 / Match_2v2)**:
   - In `Hierarchy > Main Camera`, add `SpectatorCameraController` (auto-added at runtime, but add in prefab for clarity) and set:
     - **Cinematic Waypoints** → ordered list of `Transform` waypoints (create an empty `SpectatorWaypoints` with child transforms along interesting angles around the map).
     - **Cinematic Move Seconds** / **Pause Seconds** → tune travel speed and dwell (default 6s/0.5s ok).
     - **Retarget Interval Seconds** → how often to refresh alive-player list (0.5s default).
     - **Occluder Mask** → layers that should fade when blocking the view (match what `LineOfSightTransparency` expects for players).
   - Ensure the camera already has `IsometricCamera` (or allow the controller to add it automatically) and that `LineOfSightTransparency` is present or allowed to be added.
4. **Cinematic path transforms**: Place 4–8 waypoints around key lanes. For smooth ping-pong motion, space them evenly and orient each transform toward the play area. The controller will loop automatically when no players are alive/spawned.
5. **Spawner/teams**: No prefab changes required—the `GamePlayerSpawner` and `Match1v1Controller` respect `ConnectionMetadata.IsSpectator` so spectators are not spawned or counted toward team size/score. Just ensure match scenes use the updated prefabs/scripts.
6. **Return-to-lobby flow**: Match controllers already disconnect all spectators on end/reset; keep existing scene unload flow. If you add new end-of-match hooks, also unsubscribe spectators there to avoid dangling references.

### Network Configuration
- Default transport: Unity Netcode UnityTransport (UTP)
- Connection mode: Direct IP (no relay)
- Port range: 50000-60000 for dynamic allocation
- Network prefab deduplication handled automatically

### Scene Flow
1. **MainMenu**: Initial client scene with server browser
2. **Lobby**: Matchmaking and player gathering
3. **Match_1v1/Match_2v2**: Actual gameplay scenes
4. **Bootstrap**: Server initialization scene
5. **Admin**: Server monitoring and control

### Server Types and Limits
- **Lobby**: 16 players max, threshold 8 (spawn new server when 8+ players)
- **1v1**: 2 players max, threshold 2
- **2v2**: 4 players max, threshold 4

### PlayFab Integration
The project includes PlayFab SDK for:
- Player data management
- Leaderboards and statistics
- Economy systems
- Cloud save functionality

## Unity Version
Unity 2023.3+ with HDRP (High Definition Render Pipeline)

## Package Dependencies
Key packages include:
- Unity Netcode for GameObjects 2.5.1
- Unity Gaming Services (Auth, Multiplayer, etc.)
- PlayFab SDK for comprehensive backend services
- Unity Input System 1.14.2
- ProBuilder for level design