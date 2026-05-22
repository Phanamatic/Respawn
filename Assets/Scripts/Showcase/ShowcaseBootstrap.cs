// Assets/Scripts/Showcase/ShowcaseBootstrap.cs
// Bypasses auth and lobby infrastructure, starts NGO in host mode,
// spawns a fully functional player with default loadout for portfolio recording.

using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Game.Net;

namespace Game.Showcase
{
    [DefaultExecutionOrder(-11000)] // run before NetworkConfigOptimizer (-10001)
    public sealed class ShowcaseBootstrap : MonoBehaviour
    {
        [Header("Player")]
        [SerializeField] NetworkObject playerPrefab;
        [SerializeField] Transform spawnPoint;
        [SerializeField] float spawnLift = 1.0f;

        /// <summary>
        /// When true, ClientBootstrap and NetBootstrap skip their normal flows
        /// so the showcase scene can run in isolation.
        /// </summary>
        public static bool Active { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void DetectShowcaseScene()
        {
            // Mark active if the loaded scene is a showcase scene.
            // This runs before ClientBootstrap.ForceAccountScene so
            // the guard there can check this flag.
            var sceneName = SceneManager.GetActiveScene().name;
            Active = sceneName.StartsWith("Showcase", System.StringComparison.OrdinalIgnoreCase);
        }

        IEnumerator Start()
        {
            Active = true;

            // Wait for NetworkManager singleton (created by EnsureSingleNetworkManager
            // or the NetworkManager in this scene).
            float timeout = 10f;
            while (!NetworkManager.Singleton && timeout > 0f)
            {
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            var nm = NetworkManager.Singleton;
            if (!nm)
            {
                Debug.LogError("[Showcase] NetworkManager.Singleton not found. " +
                               "Add a NetworkManager with UnityTransport to the scene.");
                yield break;
            }

            // Configure session as a 1v1 match so phase logic treats this correctly.
            SessionContext.Configure(ServerType.OneVOne, 2, 2);

            // Start host mode: this process is both server and client.
            if (!nm.StartHost())
            {
                Debug.LogError("[Showcase] Failed to start host.");
                yield break;
            }

            Debug.Log("[Showcase] Host started. Spawning player...");

            // Wait one frame for the host to fully initialize.
            yield return null;

            // Resolve spawn position.
            Vector3 spawnPos = spawnPoint ? spawnPoint.position : transform.position;
            spawnPos.y += spawnLift;
            Quaternion spawnRot = spawnPoint ? spawnPoint.rotation : Quaternion.identity;

            // Instantiate and spawn the player.
            if (!playerPrefab)
            {
                Debug.LogError("[Showcase] No playerPrefab assigned on ShowcaseBootstrap.");
                yield break;
            }

            var inst = Instantiate(playerPrefab, spawnPos, spawnRot);

            // Seed phase to Match before spawning (critical for FOV, LOS, weapon equip).
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn)
            {
                pn.SeedPhasePreSpawnServer(PlayerPhase.Match);
            }

            inst.SpawnAsPlayerObject(nm.LocalClientId);

            Debug.Log("[Showcase] Player spawned. Equipping default loadout...");

            // Wait another frame for OnNetworkSpawn to execute.
            yield return null;

            // Force equip the default loadout (AR / Pistol / Knife / Grenade).
            if (pn)
            {
                pn.ServerEnsureLoadoutValidAndEquip();
            }

            Debug.Log("[Showcase] Setup complete. Ready for recording.");
        }

        void OnDestroy()
        {
            // Only clear if this specific instance is being destroyed.
            // Prevents stale flag if the scene is unloaded.
            Active = false;
        }
    }
}
