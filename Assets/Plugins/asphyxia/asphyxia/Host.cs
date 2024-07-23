//------------------------------------------------------------
// あなたたちを許すことはできません
// Copyright © 2024 怨靈. All rights reserved.
//------------------------------------------------------------

#if UNITY_2021_3_OR_NEWER || GODOT
using System;
using System.Collections.Generic;
using System.Threading;
#endif
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static System.Net.Sockets.Socket;
using static asphyxia.Settings;
using static System.Runtime.InteropServices.Marshal;
using static KCP.KCPBASIC;

#pragma warning disable CS8600
#pragma warning disable CS8603
#pragma warning disable CS8618
#pragma warning disable CS8625
#pragma warning disable CS8632

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
        private Socket _socket;

        /// <summary>
        ///     Buffer
        /// </summary>
        private readonly byte[] _socketBuffer = new byte[BUFFER_SIZE];

        /// <summary>
        ///     Buffer
        /// </summary>
        private byte* _receiveBuffer;

        /// <summary>
        ///     Buffer
        /// </summary>
        private byte* _sendBuffer;

        /// <summary>
        ///     Buffer
        /// </summary>
        private byte* _outputBuffer;

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
        private readonly Queue<uint> _idPool = new(MAX_PEERS);

        /// <summary>
        ///     Peers
        /// </summary>
        private readonly Dictionary<int, Peer> _peers = new(MAX_PEERS);

        /// <summary>
        ///     Sentinel
        /// </summary>
        private Peer? _sentinel;

        /// <summary>
        ///     Outgoing commands
        /// </summary>
        private readonly Queue<OutgoingCommand> _outgoingCommands = new(MAX_SEND_EVENTS);

        /// <summary>
        ///     NetworkEvents
        /// </summary>
        private readonly Queue<NetworkEvent> _networkEvents = new(MAX_RECEIVE_EVENTS);

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
        ///     Disposed
        /// </summary>
        private int _disposed;

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
                FreeHGlobal((nint)_outputBuffer);
                _maxPeers = 0;
                _id = 0;
                _idPool.Clear();
                _peers.Clear();
                _sentinel = null;
                while (_outgoingCommands.TryDequeue(out var outgoingCommand))
                    outgoingCommand.Dispose();
                while (_networkEvents.TryDequeue(out var networkEvent))
                {
                    if (networkEvent.EventType != NetworkEventType.Data)
                        continue;
                    networkEvent.Packet.Dispose();
                }

                _peer = null;
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                    return;
                GC.SuppressFinalize(this);
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
        public void Create(int maxPeers, ushort port = 0, bool ipv6 = false)
        {
            lock (_lock)
            {
                if (IsSet)
                    throw new InvalidOperationException("Host has created.");
                if (maxPeers < 0 || maxPeers > MAX_PEERS)
                    throw new ArgumentOutOfRangeException(nameof(maxPeers));
                if (maxPeers == 0)
                    maxPeers = 1;
                if (!OSSupportsIPv6)
                    ipv6 = false;
                IPEndPoint localEndPoint;
                if (ipv6)
                {
                    _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        _socket.IOControl(-1744830452, new byte[1], null);
                    localEndPoint = new IPEndPoint(IPAddress.IPv6Any, port);
                    if (_remoteEndPoint == null || _remoteEndPoint.AddressFamily != AddressFamily.InterNetworkV6)
                        _remoteEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);
                }
                else
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    localEndPoint = new IPEndPoint(IPAddress.Any, port);
                    if (_remoteEndPoint == null || _remoteEndPoint.AddressFamily != AddressFamily.InterNetwork)
                        _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                }

                _socket.SendBufferSize = SOCKET_BUFFER_SIZE;
                _socket.ReceiveBufferSize = SOCKET_BUFFER_SIZE;
                try
                {
                    _socket.Bind(localEndPoint);
                }
                catch
                {
                    _socket.Dispose();
                    _socket = null;
                    throw;
                }

                _socket.Blocking = false;
                _receiveBuffer = (byte*)AllocHGlobal(BUFFER_SIZE);
                _sendBuffer = (byte*)AllocHGlobal(BUFFER_SIZE);
                _outputBuffer = (byte*)AllocHGlobal(OUTPUT_BUFFER_SIZE);
                _maxPeers = maxPeers;
                if (Interlocked.CompareExchange(ref _disposed, 0, 1) != 1)
                    return;
                GC.ReRegisterForFinalize(this);
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
            peer = new Peer(conversationId, this, _idPool.TryDequeue(out var id) ? id : _id++, remoteEndPoint, _sendBuffer, _outputBuffer, PeerState.Connecting);
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
            Insert(new OutgoingCommand(remoteEndPoint, _sendBuffer, 1));
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
                    count = _socket.ReceiveFrom(_socketBuffer, 0, BUFFER_SIZE, SocketFlags.None, ref _remoteEndPoint);
                }
                catch
                {
                    continue;
                }

                if (count <= 0)
                    break;
                var hashCode = _remoteEndPoint.GetHashCode();
                try
                {
                    if (count < (int)REVERSED_HEAD + (int)OVERHEAD)
                    {
                        if (count == 8 && _socketBuffer[0] == (byte)Header.Disconnect && _socketBuffer[1] == (byte)Header.DisconnectAcknowledge && _socketBuffer[2] == (byte)Header.Disconnect && _socketBuffer[3] == (byte)Header.DisconnectAcknowledge)
                        {
                            var conversationId = Unsafe.ReadUnaligned<uint>(ref _socketBuffer[4]);
                            if (_peer == null || hashCode != remoteEndPoint)
                            {
                                if (_peers.TryGetValue(_remoteEndPoint.GetHashCode(), out _peer))
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
                            var conversationId = Unsafe.ReadUnaligned<uint>(ref _socketBuffer[0]);
                            _peer = new Peer(conversationId, this, _idPool.TryDequeue(out var id) ? id : _id++, _remoteEndPoint, _sendBuffer, _outputBuffer);
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

            var node = _sentinel;
            while (node != null)
            {
                node.Service(_receiveBuffer);
                node = node.Next;
            }
        }

        /// <summary>
        ///     Flush
        /// </summary>
        public void Flush()
        {
            while (_outgoingCommands.TryDequeue(out var outgoingCommand))
            {
                outgoingCommand.CopyTo(_socketBuffer);
                try
                {
                    _socket.SendTo(_socketBuffer, 0, outgoingCommand.Length, SocketFlags.None, outgoingCommand.IPEndPoint);
                }
                catch
                {
                    //
                }

                outgoingCommand.Dispose();
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
        /// <param name="outgoingCommand">OutgoingCommand</param>
        internal void Insert(in OutgoingCommand outgoingCommand) => _outgoingCommands.Enqueue(outgoingCommand);

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