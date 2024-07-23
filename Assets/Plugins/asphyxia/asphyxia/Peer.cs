//------------------------------------------------------------
// あなたたちを許すことはできません
// Copyright © 2024 怨靈. All rights reserved.
//------------------------------------------------------------

#if UNITY_2021_3_OR_NEWER || GODOT
using System;
#endif
using System.Net;
using System.Runtime.InteropServices;
using KCP;
using static asphyxia.Settings;
using static asphyxia.Time;
using static asphyxia.PeerState;
using static asphyxia.Header;
using static System.Runtime.CompilerServices.Unsafe;

#pragma warning disable CS8602
#pragma warning disable CS8632

// ReSharper disable ConvertIfStatementToSwitchStatement
// ReSharper disable ConvertToAutoPropertyWithPrivateSetter
// ReSharper disable PossibleNullReferenceException

namespace asphyxia
{
    /// <summary>
    ///     Peer
    /// </summary>
    public sealed unsafe class Peer : IKcpCallback
    {
        /// <summary>
        ///     Previous
        /// </summary>
        internal Peer? Previous;

        /// <summary>
        ///     Next
        /// </summary>
        internal Peer? Next;

        /// <summary>
        ///     Host
        /// </summary>
        private readonly Host _host;

        /// <summary>
        ///     Id
        /// </summary>
        public readonly uint Id;

        /// <summary>
        ///     IPEndPoint
        /// </summary>
        public readonly IPEndPoint IPEndPoint;

        /// <summary>
        ///     Kcp
        /// </summary>
        private readonly Kcp _kcp;

        /// <summary>
        ///     Buffer
        /// </summary>
        private readonly byte* _sendBuffer;

        /// <summary>
        ///     Buffer
        /// </summary>
        private readonly byte* _outputBuffer;

        /// <summary>
        ///     Last send timestamp
        /// </summary>
        private uint _lastSendTimestamp;

        /// <summary>
        ///     Last receive timestamp
        /// </summary>
        private uint _lastReceiveTimestamp;

        /// <summary>
        ///     Peer state
        /// </summary>
        private PeerState _state;

        /// <summary>
        ///     Next update Timestamp
        /// </summary>
        private uint _nextUpdateTimestamp;

        /// <summary>
        ///     Disconnecting
        /// </summary>
        private bool _disconnecting;

        /// <summary>
        ///     Structure
        /// </summary>
        /// <param name="conversationId">ConversationId</param>
        /// <param name="host">Host</param>
        /// <param name="id">Id</param>
        /// <param name="ipEndPoint">IPEndPoint</param>
        /// <param name="sendBuffer">Buffer</param>
        /// <param name="outputBuffer">Buffer</param>
        /// <param name="state">State</param>
        internal Peer(uint conversationId, Host host, uint id, EndPoint ipEndPoint, byte* sendBuffer, byte* outputBuffer, PeerState state = PeerState.None)
        {
            _host = host;
            Id = id;
            IPEndPoint = (IPEndPoint)ipEndPoint;
            _sendBuffer = sendBuffer;
            _outputBuffer = outputBuffer;
            _state = state;
            _kcp = new Kcp(conversationId, this);
            _kcp.SetNoDelay(NO_DELAY, TICK_INTERVAL, FAST_RESEND, NO_CONGESTION_WINDOW);
            _kcp.SetWindowSize(WINDOW_SIZE, WINDOW_SIZE);
            _kcp.SetMtu(MAXIMUM_TRANSMISSION_UNIT);
            var current = Current;
            _lastSendTimestamp = current;
            _lastReceiveTimestamp = current;
        }

        /// <summary>
        ///     Is created
        /// </summary>
        public bool IsSet => _kcp.IsSet;

        /// <summary>
        ///     Peer state
        /// </summary>
        public PeerState State => _state;

        /// <summary>
        ///     Smoothed round-trip time
        /// </summary>
        public int RoundTripTime => _kcp.RxSrtt;

        /// <summary>
        ///     Output
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        void IKcpCallback.Output(byte* buffer, int length) => Output(buffer, length);

        /// <summary>
        ///     Input
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        internal void Input(byte[] buffer, int length)
        {
            if (_kcp.Input(buffer, length) != 0)
                return;
            _nextUpdateTimestamp = 0;
            if (_state != Connected)
                return;
            _lastReceiveTimestamp = Current;
        }

        /// <summary>
        ///     Output
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        private void Output(byte* buffer, int length)
        {
            _lastSendTimestamp = Current;
            _host.Insert(new OutgoingCommand(IPEndPoint, buffer, length));
            if (_disconnecting && _kcp.SendQueueCount == 0)
            {
                _host.Insert(new NetworkEvent(NetworkEventType.Disconnect, this));
                _state = Disconnected;
                _kcp.Dispose();
                _host.Remove(IPEndPoint.GetHashCode(), this);
            }
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        internal int SendInternal(byte* buffer, int length)
        {
            _nextUpdateTimestamp = 0;
            return _kcp.Send(buffer, length);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <returns>Send bytes</returns>
        public int Send(byte[] buffer)
        {
            if (_state != Connected)
                return -1;
            var length = buffer.Length;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(ref *(_sendBuffer + 1), ref buffer[0], (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        /// <returns>Send bytes</returns>
        public int Send(byte[] buffer, int length)
        {
            if (_state != Connected)
                return -1;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(ref *(_sendBuffer + 1), ref buffer[0], (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="offset">Offset</param>
        /// <param name="length">Length</param>
        /// <returns>Send bytes</returns>
        public int Send(byte[] buffer, int offset, int length)
        {
            if (_state != Connected)
                return -1;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(ref *(_sendBuffer + 1), ref buffer[offset], (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <returns>Send bytes</returns>
        public int Send(ReadOnlySpan<byte> buffer)
        {
            if (_state != Connected)
                return -1;
            var length = buffer.Length;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(ref *(_sendBuffer + 1), ref MemoryMarshal.GetReference(buffer), (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <returns>Send bytes</returns>
        public int Send(ReadOnlyMemory<byte> buffer)
        {
            if (_state != Connected)
                return -1;
            var length = buffer.Length;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(ref *(_sendBuffer + 1), ref MemoryMarshal.GetReference(buffer.Span), (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <returns>Send bytes</returns>
        public int Send(ArraySegment<byte> buffer)
        {
            if (_state != Connected)
                return -1;
            var length = buffer.Count;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(ref *(_sendBuffer + 1), ref buffer.Array[buffer.Offset], (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">Length</param>
        /// <returns>Send bytes</returns>
        public int Send(byte* buffer, int length)
        {
            if (_state != Connected)
                return -1;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(_sendBuffer + 1, buffer, (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Send
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="offset">Offset</param>
        /// <param name="length">Length</param>
        /// <returns>Send bytes</returns>
        public int Send(byte* buffer, int offset, int length)
        {
            if (_state != Connected)
                return -1;
            _sendBuffer[0] = (byte)Data;
            CopyBlock(_sendBuffer + 1, buffer + offset, (uint)length);
            return SendInternal(_sendBuffer, length + 1);
        }

        /// <summary>
        ///     Timeout
        /// </summary>
        private void Timeout()
        {
            if (_state == Connected || _state == Disconnecting)
                _host.Insert(new NetworkEvent(NetworkEventType.Disconnect, this));
            else if (_state == Connecting)
                _host.Insert(new NetworkEvent(NetworkEventType.Timeout, this));
            _state = Disconnected;
            _kcp.Dispose();
            _host.Remove(IPEndPoint.GetHashCode(), this);
        }

        /// <summary>
        ///     Try disconnect now
        /// </summary>
        internal void TryDisconnectNow(uint conversationId)
        {
            if (_kcp.ConversationId != conversationId || _state == Disconnected)
                return;
            if (_state == Connected)
                _host.Insert(new NetworkEvent(NetworkEventType.Disconnect, this));
            _state = Disconnected;
            _kcp.Dispose();
            _host.Remove(IPEndPoint.GetHashCode(), this);
        }

        /// <summary>
        ///     Disconnect
        /// </summary>
        private void DisconnectInternal()
        {
            if (_state == Disconnected)
                return;
            if (_state == Connected)
                _host.Insert(new NetworkEvent(NetworkEventType.Disconnect, this));
            _state = Disconnected;
            _kcp.Dispose();
            _host.Remove(IPEndPoint.GetHashCode(), this);
        }

        /// <summary>
        ///     Send disconnect now
        /// </summary>
        private void SendDisconnectNow()
        {
            _state = Disconnected;
            var conv = _kcp.ConversationId;
            _kcp.Flush(_outputBuffer);
            _kcp.Dispose();
            _sendBuffer[0] = (byte)Header.Disconnect;
            _sendBuffer[1] = (byte)DisconnectAcknowledge;
            _sendBuffer[2] = (byte)Header.Disconnect;
            _sendBuffer[3] = (byte)DisconnectAcknowledge;
            *(uint*)(_sendBuffer + 4) = conv;
            Output(_sendBuffer, 8);
            _host.Remove(IPEndPoint.GetHashCode(), this);
        }

        /// <summary>
        ///     Disconnect
        /// </summary>
        public void Disconnect()
        {
            if (_state == Connected)
            {
                _state = Disconnecting;
                _sendBuffer[0] = (byte)Header.Disconnect;
                SendInternal(_sendBuffer, 1);
                return;
            }

            if (_state == Disconnecting || _state == Disconnected)
                return;
            SendDisconnectNow();
        }

        /// <summary>
        ///     Disconnect now
        /// </summary>
        public void DisconnectNow()
        {
            if (_state == Disconnecting || _state == Disconnected)
                return;
            if (_state == Connected)
                _host.Insert(new NetworkEvent(NetworkEventType.Disconnect, this));
            SendDisconnectNow();
        }

        /// <summary>
        ///     Disconnect later
        /// </summary>
        public void DisconnectLater()
        {
            if (_state != Connected)
                return;
            _state = Disconnecting;
            _sendBuffer[0] = (byte)Header.Disconnect;
            SendInternal(_sendBuffer, 1);
        }

        /// <summary>
        ///     Service
        /// </summary>
        /// <param name="buffer">Receive buffer</param>
        internal void Service(byte* buffer)
        {
            if (_kcp.State == -1)
            {
                DisconnectInternal();
                return;
            }

            if (_lastReceiveTimestamp + RECEIVE_TIMEOUT <= Current)
            {
                Timeout();
                return;
            }

            while (true)
            {
                var received = _kcp.Receive(buffer, BUFFER_SIZE);
                if (received < 0)
                {
                    if (received != -1)
                    {
                        DisconnectInternal();
                        return;
                    }

                    break;
                }

                var header = buffer[0];
                if (header != 0 && header <= 64 && (header & (header - 1)) == 0)
                {
                    switch (header)
                    {
                        case (byte)Ping:
                            if (_state != Connected)
                                goto error;
                            continue;
                        case (byte)Connect:
                            if (_state != PeerState.None)
                                goto error;
                            _state = ConnectAcknowledging;
                            buffer[0] = (byte)ConnectAcknowledge;
                            SendInternal(buffer, 1);
                            continue;
                        case (byte)ConnectAcknowledge:
                            if (_state != Connecting)
                                goto error;
                            _state = Connected;
                            _host.Insert(new NetworkEvent(NetworkEventType.Connect, this));
                            buffer[0] = (byte)ConnectEstablish;
                            SendInternal(buffer, 1);
                            continue;
                        case (byte)ConnectEstablish:
                            if (_state != ConnectAcknowledging)
                                goto error;
                            _state = Connected;
                            _host.Insert(new NetworkEvent(NetworkEventType.Connect, this));
                            continue;
                        case (byte)Data:
                            if (_state != Connected && _state != Disconnecting)
                                goto error;
                            _host.Insert(new NetworkEvent(NetworkEventType.Data, this, DataPacket.Create(buffer + 1, received - 1)));
                            continue;
                        case (byte)Header.Disconnect:
                            if (_state != Connected)
                                goto error;
                            _state = Disconnected;
                            _disconnecting = true;
                            buffer[0] = (byte)DisconnectAcknowledge;
                            SendInternal(buffer, 1);
                            continue;
                        case (byte)DisconnectAcknowledge:
                            if (_state != Disconnecting)
                                goto error;
                            _host.Insert(new NetworkEvent(NetworkEventType.Disconnect, this));
                            _kcp.Dispose();
                            _host.Remove(IPEndPoint.GetHashCode(), this);
                            return;
                        default:
                            error:
                            DisconnectInternal();
                            return;
                    }
                }
            }

            var current = Current;
            if (_state == Connected && _lastSendTimestamp + PING_INTERVAL <= current)
            {
                _lastSendTimestamp = current;
                buffer[0] = (byte)Ping;
                SendInternal(buffer, 1);
            }

            if (current >= _nextUpdateTimestamp)
            {
                _kcp.Update(current, _outputBuffer);
                if (_kcp.IsSet)
                    _nextUpdateTimestamp = _kcp.Check(current);
            }
        }
    }
}