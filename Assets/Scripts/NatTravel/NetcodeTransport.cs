using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using UnityEngine;
using NetworkEvent = Unity.Netcode.NetworkEvent;

// ReSharper disable RedundantExplicitArraySize
// ReSharper disable InconsistentNaming
// ReSharper disable PossibleNullReferenceException

namespace Netcode
{
    public sealed class NetcodeTransport : UnityTransport
    {
        public ulong ServiceId;
        public ConnectionAddressData ServiceData;
        private FieldInfo _driverFieldInfo;
        private TransportEventDelegate _connectionManagerEvent;
        private NetworkDriver _driver => (NetworkDriver)_driverFieldInfo.GetValue(this);

        private void Start() => _driverFieldInfo = typeof(UnityTransport).GetField("m_Driver", BindingFlags.Instance | BindingFlags.NonPublic);

        public override void Initialize(NetworkManager networkManager = null)
        {
            base.Initialize(networkManager);
            var connectionManager = (NetworkConnectionManager)typeof(NetworkManager).GetField("ConnectionManager", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(networkManager);
            var methodInfo = typeof(NetworkConnectionManager).GetMethod("HandleNetworkEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            _connectionManagerEvent = (TransportEventDelegate)Delegate.CreateDelegate(typeof(TransportEventDelegate), connectionManager, methodInfo);
            var eventInfo = typeof(NetworkTransport).GetEvent("OnTransportEvent", BindingFlags.Instance | BindingFlags.Public);
            var fieldInfo = typeof(NetworkTransport).GetField(eventInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
            fieldInfo.SetValue(this, null);
            OnTransportEvent += HandleNetworkEvent;
        }

        public override void Shutdown()
        {
            base.Shutdown();
            OnTransportEvent -= HandleNetworkEvent;
        }

        private void HandleNetworkEvent(NetworkEvent eventType, ulong clientId, ArraySegment<byte> payload, float receiveTime)
        {
            if (clientId == ServiceId)
            {
                return;
            }

            _connectionManagerEvent(eventType, clientId, payload, receiveTime);
        }

        private static ulong ParseClientId(NetworkConnection utpConnectionId) => Unsafe.As<NetworkConnection, ulong>(ref utpConnectionId);

        public override bool StartServer()
        {
            if (_driver.IsCreated)
                return false;
            var succeeded = ServerBindAndListen(ConnectionData.ListenEndPoint);
            if (!succeeded && _driver.IsCreated)
                _driver.Dispose();
            return succeeded;
        }

        private bool ServerBindAndListen(NetworkEndPoint endPoint)
        {
            if (endPoint.Family == NetworkFamily.Invalid)
            {
                Debug.LogError($"Network listen address ({ConnectionData.Address}) is {nameof(NetworkFamily.Invalid)}!");
                return false;
            }

            typeof(UnityTransport).GetMethod("InitDriver", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, null);
            var result = _driver.Bind(endPoint);
            if (result != 0)
            {
                Debug.LogError("Server failed to bind. This is usually caused by another process being bound to the same port.");
                return false;
            }

            var serviceConnection = _driver.Connect(ServiceData.ServerEndPoint);
            result = _driver.Listen();
            if (result != 0)
            {
                Debug.LogError("Server failed to listen.");
                return false;
            }

            var fieldInfo = typeof(UnityTransport).GetField("m_State", BindingFlags.Instance | BindingFlags.NonPublic);
            ServiceId = ParseClientId(serviceConnection);
            fieldInfo.SetValue(this, 1);
            return true;
        }

        public override bool StartClient()
        {
            if (_driver.IsCreated)
                return false;
            var succeeded = ClientBindAndConnect();
            if (!succeeded && _driver.IsCreated)
                _driver.Dispose();
            return succeeded;
        }

        private bool ClientBindAndConnect()
        {
            var serverEndpoint = ConnectionData.ServerEndPoint;
            if (serverEndpoint.Family == NetworkFamily.Invalid)
            {
                Debug.LogError($"Target server network address ({ConnectionData.Address}) is {nameof(NetworkFamily.Invalid)}!");
                return false;
            }

            typeof(UnityTransport).GetMethod("InitDriver", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, null);
            var bindEndpoint = serverEndpoint.Family == NetworkFamily.Ipv6 ? NetworkEndPoint.AnyIpv6 : NetworkEndPoint.AnyIpv4;
            var result = _driver.Bind(bindEndpoint);
            if (result != 0)
            {
                Debug.LogError("Client failed to bind");
                return false;
            }

            var serviceConnection = _driver.Connect(ServiceData.ServerEndPoint);
            var serverConnection = _driver.Connect(serverEndpoint);
            var fieldInfo = typeof(UnityTransport).GetField("m_ServerClientId", BindingFlags.Instance | BindingFlags.NonPublic);
            ServiceId = ParseClientId(serviceConnection);
            fieldInfo.SetValue(this, ParseClientId(serverConnection));
            return true;
        }
    }
}