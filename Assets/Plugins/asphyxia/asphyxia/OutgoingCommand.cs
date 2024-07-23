//------------------------------------------------------------
// あなたたちを許すことはできません
// Copyright © 2024 怨靈. All rights reserved.
//------------------------------------------------------------

#if UNITY_2021_3_OR_NEWER || GODOT
using System;
#endif
using System.Net;
using static System.Runtime.InteropServices.Marshal;
using static System.Runtime.CompilerServices.Unsafe;

namespace asphyxia
{
    /// <summary>
    ///     OutgoingCommand
    /// </summary>
    internal unsafe struct OutgoingCommand : IDisposable
    {
        /// <summary>
        ///     IPEndPoint
        /// </summary>
        public EndPoint IPEndPoint;

        /// <summary>
        ///     Data
        /// </summary>
        public byte* Data;

        /// <summary>
        ///     Length
        /// </summary>
        public int Length;

        /// <summary>
        ///     Structure
        /// </summary>
        /// <param name="ipEndPoint">IPEndPoint</param>
        /// <param name="buffer">Data</param>
        /// <param name="length">Length</param>
        public OutgoingCommand(EndPoint ipEndPoint, byte* buffer, int length)
        {
            IPEndPoint = ipEndPoint;
            Data = (byte*)AllocHGlobal(length);
            CopyBlock(Data, buffer, (uint)length);
            Length = length;
        }

        /// <summary>
        ///     CopyTo
        /// </summary>
        /// <param name="dst">Destination</param>
        public void CopyTo(byte[] dst) => CopyBlock(ref dst[0], ref *Data, (uint)Length);

        /// <summary>
        ///     Dispose
        /// </summary>
        public void Dispose() => FreeHGlobal((nint)Data);
    }
}