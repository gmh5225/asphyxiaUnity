//------------------------------------------------------------
// あなたたちを許すことはできません
// Copyright © 2024 怨靈. All rights reserved.
//------------------------------------------------------------

#if UNITY_2021_3_OR_NEWER || GODOT
using System;
using System.Collections.Generic;
#endif
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static asphyxia.Settings;
using static System.Runtime.CompilerServices.Unsafe;
using static System.Runtime.InteropServices.Marshal;
using static KCP.KCPBASIC;
using static asphyxia.Time;

#pragma warning disable CA1816
#pragma warning disable CS0162
#pragma warning disable CS8600
#pragma warning disable CS8602
#pragma warning disable CS8603
#pragma warning disable CS8618
#pragma warning disable CS8625
#pragma warning disable CS8632

// ReSharper disable RedundantIfElseBlock
// ReSharper disable HeuristicUnreachableCode
// ReSharper disable PossibleNullReferenceException

namespace asphyxia
{
    /// <summary>
    ///     Host
    /// </summary>
    public sealed unsafe class Host : IDisposable
    {
        /// <summary>
        ///     Socket
        /// </summary>
        private Socket? _socket;

        /// <summary>
        ///     Socket buffer
        /// </summary>
        private readonly byte[] _socketBuffer = new byte[SOCKET_BUFFER_SIZE];

        /// <summary>
        ///     Receive buffer
        /// </summary>
        private byte* _receiveBuffer;

        /// <summary>
        ///     Send buffer
        /// </summary>
        private byte* _sendBuffer;

        /// <summary>
        ///     Flush buffer
        /// </summary>
        private byte* _flushBuffer;

        /// <summary>
        ///     Max peers
        /// </summary>
        private int _maxPeers;

        /// <summary>
        ///     Id
        /// </summary>
        private uint _id;

        /// <summary>
        ///     Id pool
        /// </summary>
        private readonly Queue<uint> _idPool = new();

        /// <summary>
        ///     Sentinel
        /// </summary>
        private Peer? _sentinel;

        /// <summary>
        ///     Peers
        /// </summary>
        private readonly Dictionary<int, Peer> _peers = new();

        /// <summary>
        ///     NetworkEvents
        /// </summary>
        private readonly Queue<NetworkEvent> _networkEvents = new();

        /// <summary>
        ///     Remote endPoint
        /// </summary>
        private EndPoint _remoteEndPoint;

        /// <summary>
        ///     Peer
        /// </summary>
        private Peer? _peer;

        /// <summary>
        ///     State lock
        /// </summary>
        private readonly object _lock = new();

        /// <summary>
        ///     Is created
        /// </summary>
        public bool IsSet => _socket != null;

        /// <summary>
        ///     LocalEndPoint
        /// </summary>
        public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint;

        /// <summary>
        ///     Dispose
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (!IsSet)
                    return;
                _socket.Close();
                _socket = null;
                FreeHGlobal((nint)_receiveBuffer);
                FreeHGlobal((nint)_sendBuffer);
                FreeHGlobal((nint)_flushBuffer);
                _maxPeers = 0;
                _id = 0;
                _idPool.Clear();
                _peers.Clear();
                _sentinel = null;
                while (_networkEvents.TryDequeue(out var networkEvent))
                {
                    if (networkEvent.EventType != NetworkEventType.Data)
                        continue;
                    networkEvent.Packet.Dispose();
                }

                _peer = null;
            }
        }

        /// <summary>
        ///     Destructure
        /// </summary>
        ~Host() => Dispose();

        /// <summary>
        ///     Create
        /// </summary>
        /// <param name="maxPeers">Max peers</param>
        /// <param name="port">Port</param>
        /// <param name="ipv6">DualMode</param>
        public SocketError Create(int maxPeers, ushort port = 0, bool ipv6 = false)
        {
            lock (_lock)
            {
                if (IsSet)
                    return SocketError.InvalidArgument;
                if (ipv6 && !Socket.OSSupportsIPv6)
                    return SocketError.SocketNotSupported;
                IPEndPoint localEndPoint;
                if (ipv6)
                {
                    _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        _socket.IOControl(-1744830452, new byte[1], null);
                    localEndPoint = new IPEndPoint(IPAddress.IPv6Any, port);
                }
                else
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    localEndPoint = new IPEndPoint(IPAddress.Any, port);
                }

                try
                {
                    _socket.Bind(localEndPoint);
                }
                catch
                {
                    _socket.Dispose();
                    _socket = null;
                    return SocketError.AddressAlreadyInUse;
                }

                if (ipv6)
                {
                    if (_remoteEndPoint == null || _remoteEndPoint.AddressFamily != AddressFamily.InterNetworkV6)
                        _remoteEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);
                }
                else
                {
                    if (_remoteEndPoint == null || _remoteEndPoint.AddressFamily != AddressFamily.InterNetwork)
                        _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                }

                if (maxPeers <= 0)
                    maxPeers = 1;
                var socketBufferSize = maxPeers * SOCKET_BUFFER_SIZE;
                if (socketBufferSize < 8388608)
                    socketBufferSize = 8388608;
                _socket.SendBufferSize = socketBufferSize;
                _socket.ReceiveBufferSize = socketBufferSize;
                _socket.Blocking = false;
                _idPool.EnsureCapacity(maxPeers);
                _peers.EnsureCapacity(maxPeers);
                var maxReceiveEvents = maxPeers << 1;
                _networkEvents.EnsureCapacity(maxReceiveEvents);
                _receiveBuffer = (byte*)AllocHGlobal(KCP_MESSAGE_SIZE);
                _sendBuffer = (byte*)AllocHGlobal(KCP_MESSAGE_SIZE);
                _flushBuffer = (byte*)AllocHGlobal(KCP_FLUSH_BUFFER_SIZE);
                _maxPeers = maxPeers;
                return SocketError.Success;
            }
        }

        /// <summary>
        ///     Check events
        /// </summary>
        /// <param name="networkEvent">NetworkEvent</param>
        /// <returns>Checked</returns>
        public bool CheckEvents(out NetworkEvent networkEvent) => _networkEvents.TryDequeue(out networkEvent);

        /// <summary>
        ///     Connect
        /// </summary>
        /// <param name="ipAddress">IPAddress</param>
        /// <param name="port">Port</param>
        public Peer? Connect(string ipAddress, ushort port) => !IsSet ? null : ConnectInternal(new IPEndPoint(IPAddress.Parse(ipAddress), port));

        /// <summary>
        ///     Connect
        /// </summary>
        /// <param name="remoteEndPoint">Remote endPoint</param>
        public Peer? Connect(IPEndPoint remoteEndPoint) => !IsSet ? null : ConnectInternal(remoteEndPoint);

        /// <summary>
        ///     Connect
        /// </summary>
        /// <param name="remoteEndPoint">Remote endPoint</param>
        private Peer? ConnectInternal(IPEndPoint remoteEndPoint)
        {
            if (remoteEndPoint.AddressFamily != _socket.AddressFamily)
                return null;
            var hashCode = remoteEndPoint.GetHashCode();
            if (_peers.TryGetValue(hashCode, out var peer))
                return peer;
            if (_peers.Count >= _maxPeers)
                return null;
            var buffer = stackalloc byte[4];
            RandomNumberGenerator.Fill(new Span<byte>(buffer, 4));
            var conversationId = *(uint*)buffer;
            peer = new Peer(conversationId, this, _idPool.TryDequeue(out var id) ? id : _id++, remoteEndPoint, _sendBuffer, _flushBuffer, PeerState.Connecting);
            _peers[hashCode] = peer;
            _peer ??= peer;
            if (_sentinel == null)
            {
                _sentinel = peer;
            }
            else
            {
                _sentinel.Previous = peer;
                peer.Next = _sentinel;
                _sentinel = peer;
            }

            buffer[0] = (byte)Header.Connect;
            peer.SendInternal(buffer, 1);
            return peer;
        }

        /// <summary>
        ///     Ping
        /// </summary>
        /// <param name="ipAddress">IPAddress</param>
        /// <param name="port">Port</param>
        public void Ping(string ipAddress, ushort port)
        {
            if (!IsSet)
                return;
            PingInternal(new IPEndPoint(IPAddress.Parse(ipAddress), port));
        }

        /// <summary>
        ///     Ping
        /// </summary>
        /// <param name="remoteEndPoint">Remote endPoint</param>
        public void Ping(IPEndPoint remoteEndPoint)
        {
            if (!IsSet)
                return;
            PingInternal(remoteEndPoint);
        }

        /// <summary>
        ///     Ping
        /// </summary>
        /// <param name="remoteEndPoint">Remote endPoint</param>
        private void PingInternal(IPEndPoint remoteEndPoint)
        {
            if (remoteEndPoint.AddressFamily != _socket.AddressFamily)
                return;
            _sendBuffer[0] = (byte)Header.Ping;
            Insert(remoteEndPoint, _sendBuffer, 1);
        }

        /// <summary>
        ///     Service
        /// </summary>
        public void Service()
        {
            var remoteEndPoint = _remoteEndPoint.GetHashCode();
            while (_socket.Poll(0, SelectMode.SelectRead))
            {
                int count;
                try
                {
                    count = _socket.ReceiveFrom(_socketBuffer, 0, SOCKET_BUFFER_SIZE, SocketFlags.None, ref _remoteEndPoint);
                }
                catch
                {
                    continue;
                }

                var hashCode = _remoteEndPoint.GetHashCode();
                try
                {
                    if (count < (int)REVERSED_HEAD + (int)OVERHEAD)
                    {
                        if (count == 8 && _socketBuffer[0] == (byte)Header.Disconnect && _socketBuffer[1] == (byte)Header.DisconnectAcknowledge && _socketBuffer[2] == (byte)Header.Disconnect && _socketBuffer[3] == (byte)Header.DisconnectAcknowledge)
                        {
                            var conversationId = ReadUnaligned<uint>(ref _socketBuffer[4]);
                            if (_peer == null || hashCode != remoteEndPoint)
                            {
                                if (_peers.TryGetValue(hashCode, out _peer))
                                    _peer.TryDisconnectNow(conversationId);
                            }
                            else
                            {
                                _peer.TryDisconnectNow(conversationId);
                            }
                        }

                        continue;
                    }

                    if (_peer == null || hashCode != remoteEndPoint)
                    {
                        if (!_peers.TryGetValue(hashCode, out _peer))
                        {
                            if (count != 25 || _socketBuffer[24] != (byte)Header.Connect || _peers.Count >= _maxPeers)
                                continue;
                            var conversationId = ReadUnaligned<uint>(ref _socketBuffer[0]);
                            _peer = new Peer(conversationId, this, _idPool.TryDequeue(out var id) ? id : _id++, _remoteEndPoint, _sendBuffer, _flushBuffer);
                            _peers[hashCode] = _peer;
                            if (_sentinel == null)
                            {
                                _sentinel = _peer;
                            }
                            else
                            {
                                _sentinel.Previous = _peer;
                                _peer.Next = _sentinel;
                                _sentinel = _peer;
                            }
                        }
                    }

                    _peer.Input(_socketBuffer, count);
                }
                finally
                {
                    remoteEndPoint = hashCode;
                }
            }

            var current = Current;
            var node = _sentinel;
            while (node != null)
            {
                var temp = node;
                node = node.Next;
                temp.Service(current, _receiveBuffer);
            }
        }

        /// <summary>
        ///     Flush
        /// </summary>
        public void Flush()
        {
            var current = Current;
            var node = _sentinel;
            while (node != null)
            {
                var temp = node;
                node = node.Next;
                temp.Update(current);
            }
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="networkEvent">NetworkEvent</param>
        internal void Insert(in NetworkEvent networkEvent) => _networkEvents.Enqueue(networkEvent);

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="endPoint">IPEndPoint</param>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        internal void Insert(EndPoint endPoint, byte* buffer, int length)
        {
            try
            {
#if !UNITY_2021_3_OR_NEWER || NET6_0_OR_GREATER
                _socket.SendTo(new ReadOnlySpan<byte>(buffer, length), SocketFlags.None, endPoint);
#else
                CopyBlock(ref _socketBuffer[0], ref *buffer, (uint)length);
                _socket.SendTo(_socketBuffer, 0, length, SocketFlags.None, endPoint);
#endif
            }
            catch
            {
                //
            }
        }

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="hashCode">HashCode</param>
        /// <param name="peer">Peer</param>
        internal void Remove(int hashCode, Peer peer)
        {
            if (_peer == peer)
                _peer = null;
            _idPool.Enqueue(peer.Id);
            _peers.Remove(hashCode);
            if (peer.Previous != null)
                peer.Previous.Next = peer.Next;
            else
                _sentinel = peer.Next;
            if (peer.Next != null)
                peer.Next.Previous = peer.Previous;
        }
    }
}