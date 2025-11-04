using UnityEngine;

public class RandomParticleMaterial : MonoBehaviour
{
    [Header("Particle System")]
    [SerializeField] private ParticleSystem targetParticleSystem;

    [Header("Materials")]
    [SerializeField] private Material defaultMaterial;
    [SerializeField] private Material rareMaterial;

    [Header("Rare Material Chance")]
    [Tooltip("1 in X chance for rare material to appear")]
    [SerializeField] private int rareChance = 10000;

    private void Start()
    {
        AssignRandomMaterial();
    }

    private void AssignRandomMaterial()
    {
        if (targetParticleSystem == null)
        {
            Debug.LogWarning("RandomParticleMaterial: No particle system assigned!");
            return;
        }

        if (defaultMaterial == null || rareMaterial == null)
        {
            Debug.LogWarning("RandomParticleMaterial: Materials not assigned!");
            return;
        }

        // Get the particle system renderer
        ParticleSystemRenderer particleRenderer = targetParticleSystem.GetComponent<ParticleSystemRenderer>();

        if (particleRenderer == null)
        {
            Debug.LogWarning("RandomParticleMaterial: Particle system has no renderer!");
            return;
        }

        // Roll for rare material (1 in 10000 chance)
        int randomRoll = Random.Range(0, rareChance);
        Material selectedMaterial;

        if (randomRoll == 0)
        {
            // Rare material hit!
            selectedMaterial = rareMaterial;
            Debug.Log("RandomParticleMaterial: RARE MATERIAL APPEARED! You got lucky!");
        }
        else
        {
            // Default material
            selectedMaterial = defaultMaterial;
        }

        // Assign the material to the renderer
        particleRenderer.material = selectedMaterial;
    }
}
