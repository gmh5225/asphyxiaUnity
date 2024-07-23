//------------------------------------------------------------
// あなたたちを許すことはできません
// Copyright © 2024 怨靈. All rights reserved.
//------------------------------------------------------------

#if UNITY_2021_3_OR_NEWER || GODOT
using System;
#endif
using System.Net;

namespace asphyxia
{
    /// <summary>
    ///     IPEndPoint extensions
    /// </summary>
    public static unsafe class IPEndPointExtensions
    {
        /// <summary>
        ///     Create
        /// </summary>
        /// <param name="ipEndPoint">IPEndPoint</param>
        /// <returns>DataPacket</returns>
        public static DataPacket CreateDataPacket(this IPEndPoint ipEndPoint)
        {
            var buffer = stackalloc byte[20];
            ipEndPoint.Address.TryWriteBytes(new Span<byte>(buffer, 16), out var bytesWritten);
            *(int*)(buffer + bytesWritten) = ipEndPoint.Port;
            return DataPacket.Create(buffer, bytesWritten + 4);
        }

        /// <summary>
        ///     Create
        /// </summary>
        /// <param name="ipEndPoint">IPEndPoint</param>
        /// <param name="space">Space</param>
        /// <returns>DataPacket</returns>
        public static DataPacket CreateDataPacket(this IPEndPoint ipEndPoint, int space)
        {
            var buffer = stackalloc byte[space + 20];
            buffer += space;
            ipEndPoint.Address.TryWriteBytes(new Span<byte>(buffer, 16), out var bytesWritten);
            *(int*)(buffer + bytesWritten) = ipEndPoint.Port;
            return DataPacket.Create(buffer - space, space + bytesWritten + 4);
        }
    }
}