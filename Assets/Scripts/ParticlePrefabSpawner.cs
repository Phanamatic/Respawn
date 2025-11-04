using UnityEngine;
using System.Collections.Generic;

public class ParticlePrefabSpawner : MonoBehaviour
{
    [Header("Particle System")]
    [SerializeField] private ParticleSystem targetParticleSystem;

    [Header("Prefab Settings")]
    [SerializeField] private GameObject prefabToSpawn;

    [Header("Optional Settings")]
    [Tooltip("Should the prefab follow the particle's rotation?")]
    [SerializeField] private bool matchParticleRotation = true;

    [Tooltip("Should the prefab match the particle's scale?")]
    [SerializeField] private bool matchParticleScale = true;

    private ParticleSystem.Particle[] particles;
    private Dictionary<int, GameObject> spawnedPrefabs = new Dictionary<int, GameObject>();
    private List<int> particlesToRemove = new List<int>();

    private void Start()
    {
        if (targetParticleSystem == null)
        {
            targetParticleSystem = GetComponent<ParticleSystem>();
        }

        if (targetParticleSystem == null)
        {
            Debug.LogError("ParticlePrefabSpawner: No particle system found!");
            enabled = false;
            return;
        }

        if (prefabToSpawn == null)
        {
            Debug.LogError("ParticlePrefabSpawner: No prefab assigned!");
            enabled = false;
            return;
        }

        // Initialize particle array
        particles = new ParticleSystem.Particle[targetParticleSystem.main.maxParticles];
    }

    private void LateUpdate()
    {
        if (targetParticleSystem == null || prefabToSpawn == null)
            return;

        // Get all alive particles
        int particleCount = targetParticleSystem.GetParticles(particles);

        // Track which particles are still alive
        HashSet<int> aliveParticles = new HashSet<int>();

        // Update existing prefabs and spawn new ones
        for (int i = 0; i < particleCount; i++)
        {
            int particleId = i;
            aliveParticles.Add(particleId);

            // If this particle doesn't have a prefab yet, spawn one
            if (!spawnedPrefabs.ContainsKey(particleId))
            {
                GameObject newPrefab = Instantiate(prefabToSpawn, particles[i].position, Quaternion.identity);
                spawnedPrefabs[particleId] = newPrefab;
            }

            // Update prefab position and rotation
            if (spawnedPrefabs.ContainsKey(particleId) && spawnedPrefabs[particleId] != null)
            {
                GameObject prefab = spawnedPrefabs[particleId];
                prefab.transform.position = particles[i].position;

                if (matchParticleRotation)
                {
                    prefab.transform.rotation = Quaternion.Euler(particles[i].rotation3D);
                }

                if (matchParticleScale)
                {
                    float scale = particles[i].GetCurrentSize(targetParticleSystem);
                    prefab.transform.localScale = Vector3.one * scale;
                }
            }
        }

        // Find and destroy prefabs for dead particles
        particlesToRemove.Clear();
        foreach (var kvp in spawnedPrefabs)
        {
            if (!aliveParticles.Contains(kvp.Key))
            {
                particlesToRemove.Add(kvp.Key);
                if (kvp.Value != null)
                {
                    Destroy(kvp.Value);
                }
            }
        }

        // Remove dead particles from dictionary
        foreach (int id in particlesToRemove)
        {
            spawnedPrefabs.Remove(id);
        }
    }

    private void OnDestroy()
    {
        // Clean up all spawned prefabs when this script is destroyed
        foreach (var kvp in spawnedPrefabs)
        {
            if (kvp.Value != null)
            {
                Destroy(kvp.Value);
            }
        }
        spawnedPrefabs.Clear();
    }
}
