// Minimal player identity. Colors the mesh by owner and renames the instance.

using UnityEngine;
using Unity.Netcode;

namespace Game.Net
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerAvatar : NetworkBehaviour
    {
        private Renderer[] _renderers;

        private void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
        }

        public override void OnNetworkSpawn()
        {
            gameObject.name = IsOwner ? "Player (Owner)" : $"Player ({OwnerClientId})";
            ApplyColor(ColorFromId(OwnerClientId));
        }

        private void ApplyColor(Color c)
        {
            foreach (var r in _renderers)
            {
                if (!r || r.sharedMaterial == null) continue;
                // Use a material instance so players do not share colors.
                if (!Application.isPlaying) continue;
                var inst = r.material;
                if (inst.HasProperty("_Color")) inst.SetColor("_Color", c);
            }
        }

        private static Color ColorFromId(ulong id)
        {
            // Deterministic pastel
            var h = (id * 0.6180339887f) % 1f;
            var s = 0.6f;
            var v = 0.9f;
            Color.RGBToHSV(Color.white, out _, out _, out _);
            return Color.HSVToRGB(h, s, v);
        }
    }
}
