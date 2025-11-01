# Copilot Instructions for Respawn Unity Project

## Project Overview
Respawn is a Unity multiplayer FPS game using Netcode for GameObjects with client-server architecture. Features include lobby matchmaking, 1v1/2v2 matches, and a comprehensive Line-of-Sight (LOS) fog-of-war system with server-side visibility culling and client-side rendering.

## Architecture
- **Networking**: Unity Netcode with dedicated servers, direct IP connections, multi-scene flow (MainMenu → Lobby → Match).
- **LOS System**: Server calculates visibility per client/team, clients render FOV meshes and fog overlays using stencil buffers.
- **Rendering**: HDRP pipeline with custom shaders; use UnityCG.cginc for compatibility in simple passes.
- **Components**: Singleton systems (e.g., LosVisibilitySystem), component-based player/networking (PlayerNetwork.cs).

## Key Patterns
- **Visibility Management**: Server-side `LosVisibilitySystem.Tick()` uses raycasting on "Occluder" layer to control NetworkShow/NetworkHide.
- **Client Rendering**: `FovMesh.cs` generates procedural FOV meshes; `FogOfWarOverlayPlane.cs` renders full-screen fog with stencil testing.
- **Depth Control**: `EnsureTransparentNoDepth()` in transparency scripts disables ZWrite/ZTest for faded objects to prevent depth conflicts.
- **Asset Loading**: Use `Resources.Load<Material>()` for runtime shader/material access (e.g., "LOS/FOVStencilWriteMat").
- **Material Efficiency**: Prefer `MaterialPropertyBlock` over material instances for per-renderer properties.

## Workflows
- **Building**: Use Unity Editor menu "Build/Quick/" for automated client/server builds and server management.
- **Server Launch**: Command-line args like `-serverType 1v1 -port 7778 -max 2` for dedicated servers.
- **Debugging**: Monitor server logs via PowerShell tailing; use debug rays in LOS scripts.
- **Testing**: Run in Unity Editor with multiple instances; test multiplayer via local servers.

## Conventions
- **Layers**: "Occluder" for LOS raycasting; ensure consistent layer usage.
- **Shaders**: HLSL with #pragma target 3.0; capitalize Vert/Frag functions; use UnityObjectToClipPos for transforms.
- **Networking**: RPCs for client-server sync; NetworkShow/NetworkHide for visibility; handle team-based logic in visibility systems.
- **File Structure**: Scripts in `Assets/Scripts/Networking/Runtime/`; shaders in `Assets/Shaders/LOS/`; scenes in `Assets/Scenes/Game/`.
- **Dependencies**: PlayFab for backend; Unity Gaming Services for auth/multiplayer.

Reference `PROJECT_GUIDE.md` for detailed build/server commands and architecture overview.

## VS Code Agent — Unity 6 Patch Executor
### Mission
Apply user-provided code changes verbatim to the Unity project with minimal, safe edits. Prefer small, targeted patches. Do not "improve," refactor, or reformat unless instructed.

### Environment pins
- Unity 6 (6000.0.58f2)
- C# 10/11 as per project
- Netcode for GameObjects (NGO)
- Unity Transport (UTP) direct
- URP if present
- Git repo assumed

### Ground rules
- Follow the user's instructions as written.
- Patch only the files named by the user. If a file is implied, pick the most local and obvious target.
- Do not change behavior outside the requested scope.
- No opinionated cleanup. No mass rename. No formatter runs unless asked.
- Keep encoding UTF-8, line endings LF, indentation unchanged per file.
- Add human developer comments only when the user asks for comments.

### Input format the agent expects
User may send any of the following. Support all.

**Patch block**
```
FILE: Assets/Path/Script.cs
CHANGE: Add | Edit | Remove

ANCHOR: <unique locator or code fragment>
--- BEFORE ---
<exact lines or minimal locator>
--- AFTER ---
<new or modified lines>
```

**New file**
```
FILE: Assets/Path/NewScript.cs
CHANGE: New File
--- CONTENT ---
<entire file contents>
```

**Freeform instruction**
Plain English like: "Add a public UseBrake() method to Assets/Scripts/Player/Powerups/BrakePower.cs that zeroes linear and angular velocity on server."

### Output the agent must produce
- A compact summary of changed files.
- For each updated or new C# file, also print the full final file content when the user requests it.
- If scene/inspector work is required, emit a numbered checklist.
- If compilation ran, show errors succinctly with file:line.

### Execution loop
1. Parse the request. Extract files, anchors, and actions.
2. Locate target files. If missing and CHANGE: New File, create them and ensure folder path exists.
3. Apply patches:
   - If ANCHOR present: Try exact match first.
   - If not found, do fuzzy search: Search by type/member signature. Use 3–6 lines of surrounding context to locate the block.
   - If multiple candidates, pick the smallest diff that compiles.
   - If --- BEFORE --- block present, verify it matches. If not, fall back to anchor + fuzzy.
4. Validate:
   - Run a compile check if Unity CLI is available:
     - Windows: `"<UnityEditor.exe>" -batchmode -quit -projectPath "<repo_root>" -logFile -`
     - macOS: `/Applications/Unity/Hub/Editor/6000.0.58f2/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath "<repo_root>" -logFile -`
   - If Unity not available, run static checks: Ensure no missing semicolons, unbalanced braces. Ensure namespaces compile against existing asmdef if present.
5. Report: List changed files. Print full script(s) if requested. Print any build errors with first cause.
6. Commit (only if the user allows auto-commit): Commit message format: `chore(patch): <short imperative summary> Files: <N> changed Scope: Unity6/NGO patch-only`
   - Never push unless told.

### Patch application rules
- Keep using order unchanged unless new usings are required.
- Do not reorder members or regions.
- Do not change .asmdef or Player Settings unless asked.
- For NGO: Do not toggle RequireOwnership or RPC reliability unless asked. Respect server authority patterns already in the file.

### New file rules
- Include namespace consistent with neighboring files.
- Include minimal XML summary only if the user asks for comments.
- Place under the path the user gave. If ambiguous, mirror nearby layout.

### Scene / Prefab / Inspector changes
- Do not edit .unity or .prefab YAML unless explicitly requested.
- Instead output a checklist the user can execute inside Unity. Example:
  ```
  Hierarchy > PlayerPrefab
  Inspector: Add Component > LosFader
  Set: FadeLayerMask = "Obstacles"
  ```
- If YAML edits are requested, modify only the minimal node set and preserve GUIDs.

### Error handling
- If an anchor cannot be found, state which anchor failed and what fuzzy rule was used.
- If compilation fails, revert the smallest failing hunk and report.
- If requested change conflicts with read-only or generated code, report and skip.

### Security and style
- No secrets in logs.
- Follow existing code style from file. No new stylistic conventions.

### Heuristics for anchors
- Prefer method signature anchors: `void UseBrake()` or `[ServerRpc]`.
- Secondary: field or property name anchors.
- Tertiary: comment markers if the user provided them.
- Never match inside comments unless explicitly told.

### Quick templates the agent should accept
**Edit template**
```
FILE: Assets/Scripts/Net/NetworkBootstrap.cs
CHANGE: Edit

ANCHOR: transport.UseRelay(true);
--- BEFORE ---
// Force Relay path under UGS
transport.UseRelay(true);
--- AFTER ---
// Direct UTP client→server via address from Unity Lobby Service
transport.UseRelay(false);
var utp = (Unity.Netcode.Transports.UTP.UnityTransport)NetworkManager.Singleton.NetworkConfig.NetworkTransport;
utp.SetConnectionData(publicHostFromLobby, (ushort)publicPortFromLobby);
// Server bind handled at bootstrap.
```

**New file template**
```
FILE: Assets/Scripts/Powerups/BrakePower.cs
CHANGE: New File
--- CONTENT ---
using Unity.Netcode;
using UnityEngine;

namespace Game.Powerups
{
    /// <summary>Stops player movement server-side.</summary>
    public sealed class BrakePower : NetworkBehaviour
    {
        // Zero velocity on server, client asks via ServerRpc.
        [ServerRpc(RequireOwnership = false)]
        public void UseBrakeServerRpc(NetworkObjectReference targetRef)
        {
            if (!IsServer) return;
            if (!targetRef.TryGet(out var no)) return;
            var rb = no.GetComponent<Rigidbody>();
            if (!rb) return;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }
}
```

### Full-file return rule
When the user says "return full script," print the final contents after patching. One file per code block.

### Minimal commit discipline
- Group all hunks from one user instruction in one commit.
- Message starts with the user's high-level change. Example: `chore(patch): disable Relay and set direct UTP endpoint from Lobby`

### Unity specifics the agent must respect
- Do not change Fixed Timestep, physics, or NGO tick rates unless asked.
- Do not add Relay usage. Transport remains direct UTP.
- Keep payload sizes small; do not add per-tick allocations.
- If adding RPCs, default RequireOwnership=false only when explicitly needed and validated by server.

### When to ask one clarifying question
Only if file cannot be unambiguously identified or the anchor collides in multiple places with equal confidence. Otherwise proceed with best match and report assumptions.