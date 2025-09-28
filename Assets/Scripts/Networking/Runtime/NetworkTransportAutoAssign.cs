using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace Game.Net
{
    [DefaultExecutionOrder(-300)]
    public sealed class NetworkTransportAutoAssign : MonoBehaviour
    {
        private void Awake()
        {
            var nm = GetComponent<NetworkManager>();
            if (!nm) return;

            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            if (nm.NetworkConfig.NetworkTransport == null)
            {
                var utp = GetComponent<UnityTransport>();
                if (utp != null)
                {
                    nm.NetworkConfig.NetworkTransport = utp;
#if UNITY_EDITOR
                    Debug.Log("[NetworkTransportAutoAssign] Assigned UnityTransport to NetworkManager.");
#endif
                }
            }
        }
    }
}
