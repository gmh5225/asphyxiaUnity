using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace Netcode
{
    public sealed class NetcodeService : UnityTransport
    {
        private Dictionary<ulong, KeyValuePair<string, int>> _endPoints;

        private void HandleNetworkEvent(NetworkEvent eventType, ulong clientId, ArraySegment<byte> payload, float receiveTime)
        {
            switch (eventType)
            {
                case NetworkEvent.Data:
                    break;
                case NetworkEvent.Connect:
                    break;
                case NetworkEvent.Disconnect:
                    break;
                case NetworkEvent.TransportFailure:
                    break;
                case NetworkEvent.Nothing:
                    break;
            }
        }
    }
}