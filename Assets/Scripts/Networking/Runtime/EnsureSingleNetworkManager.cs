// Assets/Scripts/Networking/Runtime/EnsureSingleNetworkManager.cs
using UnityEngine;
using Unity.Netcode;

namespace Game.Net
{
    [DisallowMultipleComponent]
    public sealed class EnsureSingleNetworkManager : MonoBehaviour
    {
        void Awake()
        {
            var nm = GetComponent<NetworkManager>();
            if (!nm) { Destroy(gameObject); return; }

            if (NetworkManager.Singleton != null && NetworkManager.Singleton != nm)
            {
                Destroy(gameObject);
                return;
            }
            DontDestroyOnLoad(gameObject);
        }
    }
}
