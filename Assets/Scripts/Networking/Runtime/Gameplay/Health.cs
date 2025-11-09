using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    [DisallowMultipleComponent]
    public sealed class Health : NetworkBehaviour
    {
        [SerializeField] private int _max = 100;
        private readonly NetworkVariable<int> _current = new NetworkVariable<int>(
            writePerm: NetworkVariableWritePermission.Server
        );

        public int Max => _max;
        public int Current => _current.Value;
        public bool HasServerAuthority => IsServer;

        public override void OnNetworkSpawn()
        {
            if (IsServer) _current.Value = _max;
        }

        public void ResetToFullServer()
        {
            if (!IsServer) return;
            _current.Value = _max;
        }

        public void ApplyDamage(int amount, ulong attackerClientId, Vector3 hitPoint)
        {
            if (!IsServer || amount <= 0) return;
            var next = Mathf.Max(0, _current.Value - amount);
            _current.Value = next;

            if (next <= 0)
            {
                var pn = GetComponentInParent<PlayerNetwork>();
                if (pn != null) pn.OnServerDied(attackerClientId, hitPoint);
            }
        }
    }
}
