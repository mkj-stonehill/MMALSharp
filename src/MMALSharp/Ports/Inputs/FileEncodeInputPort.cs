﻿// <copyright file="ImageFileDecodeInputPort.cs" company="Techyian">
// Copyright (c) Ian Auty. All rights reserved.
// Licensed under the MIT License. Please see LICENSE.txt for License info.
// </copyright>

using System;
using System.Threading.Tasks;
using MMALSharp.Common.Utility;
using MMALSharp.Components;
using MMALSharp.Native;

namespace MMALSharp.Ports.Inputs
{
    /// <summary>
    /// A custom port definition used specifically when using encoder conversion functionality.
    /// </summary>
    public unsafe class FileEncodeInputPort : InputPort
    {
        /// <summary>
        /// Creates a new instance of <see cref="FileEncodeInputPort"/>. 
        /// </summary>
        /// <param name="ptr">The native pointer.</param>
        /// <param name="comp">The component this port is associated with.</param>
        /// <param name="type">The type of port.</param>
        /// <param name="guid">Managed unique identifier for this port.</param>
        public FileEncodeInputPort(IntPtr ptr, IComponent comp, PortType type, Guid guid)
            : base(ptr, comp, type, guid)
        {
        }

        /// <summary>
        /// Creates a new instance of <see cref="FileEncodeInputPort"/>.
        /// </summary>
        /// <param name="copyFrom">The port to copy data from.</param>
        public FileEncodeInputPort(IPort copyFrom)
            : base((IntPtr)copyFrom.Ptr, copyFrom.ComponentReference, copyFrom.PortType, copyFrom.Guid)
        {
        }

        internal override void NativeInputPortCallback(MMAL_PORT_T* port, MMAL_BUFFER_HEADER_T* buffer)
        {
            if (MMALCameraConfig.Debug)
            {
                MMALLog.Logger.Debug("Releasing input port buffer");
            }
            
            var bufferImpl = new MMALBufferImpl(buffer);
            bufferImpl.Release();

            if (this.Enabled && this.BufferPool != null)
            {
                var newBuffer = this.BufferPool.Queue.GetBuffer();

                if (newBuffer != null)
                {
                    var result = this.CallbackHandler.CallbackWithResult(newBuffer);

                    if (result.EOF)
                    {
                        Task.Run(() => { this.Trigger.SetResult(true); });
                    }

                    this.SendBuffer(newBuffer);
                }
            }
        }
    }
}
