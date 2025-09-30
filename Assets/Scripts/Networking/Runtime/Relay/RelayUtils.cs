// Helper to build RelayServerData for current UTP
// Path: Assets/Scripts/Networking/Runtime/Relay/RelayUtils.cs
using System;
using System.Linq;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay.Models;

namespace Game.Net
{
    public static class RelayUtils
    {
        // Prefer DTLS. If missing, fall back to UDP. Set isSecure=true for DTLS/WSS.
        public static RelayServerData ToServerData(Allocation alloc, bool useWss = false)
        {
            var connectionType = useWss ? RelayServerEndpoint.ConnectionTypeWss : RelayServerEndpoint.ConnectionTypeDtls;
            var ep = SelectEndpoint(alloc.ServerEndpoints, connectionType);
            var secure = useWss || ep.Secure;

            return new RelayServerData(
                ep.Host,
                (ushort)ep.Port,
                alloc.AllocationIdBytes,
                alloc.ConnectionData,
                /* hostConnectionData for host == connectionData */ alloc.ConnectionData,
                alloc.Key,
                secure
            );
        }

        public static RelayServerData ToServerData(JoinAllocation join, bool useWss = false)
        {
            var connectionType = useWss ? RelayServerEndpoint.ConnectionTypeWss : RelayServerEndpoint.ConnectionTypeDtls;
            var ep = SelectEndpoint(join.ServerEndpoints, connectionType);
            var secure = useWss || ep.Secure;

            return new RelayServerData(
                ep.Host,
                (ushort)ep.Port,
                join.AllocationIdBytes,
                join.ConnectionData,
                join.HostConnectionData,
                join.Key,
                secure
            );
        }

        private static RelayServerEndpoint SelectEndpoint(System.Collections.Generic.IList<RelayServerEndpoint> endpoints, string connectionType)
        {
            var ep = endpoints.FirstOrDefault(e => e.ConnectionType == connectionType)
                  ?? endpoints.FirstOrDefault(e => e.ConnectionType == RelayServerEndpoint.ConnectionTypeUdp);
            if (ep == null) throw new InvalidOperationException("No suitable Relay endpoint found.");
            return ep;
        }
    }
}
