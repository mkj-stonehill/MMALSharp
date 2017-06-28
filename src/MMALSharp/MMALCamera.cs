﻿using MMALSharp.Components;
using MMALSharp.Native;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MMALSharp.Handlers;

namespace MMALSharp
{    
    /// <summary>
    /// This class provides an interface to the Raspberry Pi camera module. 
    /// </summary>
    public sealed class MMALCamera
    {
        /// <summary>
        /// Reference to the camera component
        /// </summary>
        public MMALCameraComponent Camera { get; set; }

        /// <summary>
        /// List of all encoders currently in the pipeline
        /// </summary>
        public List<MMALDownstreamComponent> DownstreamComponents { get; set; }

        /// <summary>
        /// List of all established connections
        /// </summary>
        public List<MMALConnectionImpl> Connections { get; set; }

        /// <summary>
        /// Reference to the Video splitter component which attaches to the Camera's video output port
        /// </summary>
        public MMALSplitterComponent Splitter { get; set; }

        private static readonly Lazy<MMALCamera> lazy = new Lazy<MMALCamera>(() => new MMALCamera());

        public static MMALCamera Instance => lazy.Value;

        private MMALCamera()
        {
            BcmHost.bcm_host_init();

            this.Camera = new MMALCameraComponent();
            this.DownstreamComponents = new List<MMALDownstreamComponent>();
            this.Connections = new List<MMALConnectionImpl>();
        }

        /// <summary>
        /// Begin capture on one of the camera's output ports.
        /// </summary>
        /// <param name="port">An output port of the camera component</param>
        public void StartCapture(MMALPortImpl port)
        {            
            if (port == this.Camera.StillPort || port == this.Camera.VideoPort)
            {
                port.SetImageCapture(true);
            }                
        }

        /// <summary>
        /// Stop capture on one of the camera's output ports
        /// </summary>
        /// <param name="port">An output port of the camera component</param>
        public void StopCapture(MMALPortImpl port)
        {
            if (port == this.Camera.StillPort || port == this.Camera.VideoPort)
            {
                port.SetImageCapture(false);
            }                
        }
        
        /// <summary>
        /// Force capture to stop on a port (Still or Video)
        /// </summary>
        /// <param name="port">The capture port</param>
        public void ForceStop(MMALPortImpl port)
        {
            if(port.Trigger.CurrentCount > 0)
            {
                port.Trigger.Signal();
            }            
        }

        /// <summary>
        /// Self-contained method for recording H.264 video for a specified amount of time. Records at 30fps, 25Mb/s at the highest quality.
        /// </summary>        
        /// <param name="handler">The video capture handler to apply to the encoder.</param>        
        /// <param name="timeout">A timeout to stop the video capture</param>
        /// <param name="split">Used for Segmented video mode</param>
        /// <returns>The awaitable Task</returns>
        public async Task TakeVideo(VideoStreamCaptureHandler handler, DateTime? timeout = null, Split split = null)
        {
            if (split != null && !MMALCameraConfig.InlineHeaders)
            {
                Helpers.PrintWarning("Inline headers not enabled. Split mode not supported when this is disabled.");
                split = null;
            }

            using (var vidEncoder = new MMALVideoEncoder(handler, new MMAL_RATIONAL_T(30, 1), timeout, split))
            using (var renderer = new MMALVideoRenderer())
            {
                vidEncoder.ConfigureOutputPort(0, MMALEncoding.MMAL_ENCODING_H264, MMALEncoding.MMAL_ENCODING_I420, 10, 25000000);

                //Create our component pipeline.         
                this.Camera.VideoPort.ConnectTo(vidEncoder);
                this.Camera.PreviewPort.ConnectTo(renderer);
                this.ConfigureCameraSettings();
                
                try
                {
                    Console.WriteLine($"Preparing to take video. Resolution: {vidEncoder.Width} x {vidEncoder.Height}. " +
                                      $"Encoder: {vidEncoder.Outputs[0].EncodingType.EncodingName}. Pixel Format: {vidEncoder.Outputs[0].PixelFormat.EncodingName}.");
                    
                    await BeginProcessing(this.Camera.VideoPort, vidEncoder);
                }
                finally
                {
                    vidEncoder.Handler.PostProcess();
                }
            }        
        }

        /// <summary>
        /// Self-contained method to capture raw image data directly from the Camera component - this method does not use an Image encoder.
        /// </summary>
        /// <returns>The awaitable Task</returns>
        public async Task TakeRawPicture(ICaptureHandler handler)
        {
            var connection = this.Connections.Where(c => c.OutputPort == this.Camera.StillPort).FirstOrDefault();
            
            if (connection != null)
            {
                throw new PiCameraError("A connection was found to the Camera still port. No encoder should be connected to the Camera's still port for raw capture.");
            }

            if (handler == null)
            {
                throw new PiCameraError("No handler specified");
            }

            this.Camera.Handler = handler;

            this.CheckPreviewComponentStatus();

            //Enable the image encoder output port.            
            try
            {
                Console.WriteLine($"Preparing to take raw picture - Resolution: {MMALCameraConfig.StillResolution.Width} x {MMALCameraConfig.StillResolution.Height}. " +
                                  $"Encoder: {MMALCameraConfig.StillEncoding.EncodingName}. Pixel Format: {MMALCameraConfig.StillSubFormat.EncodingName}.");

                //Enable processing on the camera still port.
                this.Camera.EnableConnections();

                this.Camera.Start(this.Camera.StillPort, new Action<MMALBufferImpl, MMALPortBase>(this.Camera.ManagedOutputCallback));
                this.Camera.StillPort.Trigger = new Nito.AsyncEx.AsyncCountdownEvent(1);

                this.StartCapture(this.Camera.StillPort);

                //Wait until the process is complete.
                await this.Camera.StillPort.Trigger.WaitAsync();

                //Stop capturing on the camera still port.
                this.StopCapture(this.Camera.StillPort);
                
                this.Camera.Stop(MMALCameraComponent.MMALCameraStillPort);

                //Close open connections and clean port pools.
                this.Camera.DisableConnections();

                this.Camera.CleanPortPools();
            }
            finally
            {
                this.Camera.Handler.PostProcess();
                this.Camera.Handler.Dispose();
            }
        }

        /// <summary>
        /// Self-contained method for capturing a single image from the camera still port. 
        /// An MMALImageEncoder component will be created and attached to the still port.
        /// </summary>                
        /// <param name="handler">The image capture handler to apply to the encoder component</param>
        /// <param name="encodingType">The image encoding type e.g. JPEG, BMP</param>
        /// <param name="pixelFormat">The pixel format to use with the encoder e.g. I420 (YUV420)</param>
        /// <returns>The awaitable Task</returns>
        public async Task TakePicture(ImageStreamCaptureHandler handler, MMALEncoding encodingType, MMALEncoding pixelFormat)
        {
            using (var imgEncoder = new MMALImageEncoder(handler))
            using (var renderer = new MMALNullSinkComponent())
            {
                imgEncoder.ConfigureOutputPort(0, encodingType, pixelFormat, 90);

                //Create our component pipeline.         
                this.Camera.StillPort.ConnectTo(imgEncoder);
                this.Camera.PreviewPort.ConnectTo(renderer);
                this.ConfigureCameraSettings();
                
                //Enable the image encoder output port.            
                try
                {
                    Console.WriteLine($"Preparing to take picture. Resolution: {imgEncoder.Width} x {imgEncoder.Height}. " +
                                      $"Encoder: {encodingType.EncodingName}. Pixel Format: {pixelFormat.EncodingName}.");

                    await BeginProcessing(this.Camera.StillPort, imgEncoder);
                }
                finally
                {
                    imgEncoder.Handler.PostProcess();
                }
            }
        }
        
        /// <summary>
        /// Helper method to begin processing image data. Starts the Camera port and awaits until processing is complete.
        /// Cleans up resources upon finish.
        /// </summary>
        /// <param name="cameraPort">The camera port which image data is coming from</param>
        /// <param name="handlerComponents">The handler component(s) we are processing data on</param>
        /// <returns>The awaitable Task</returns>
        public async Task BeginProcessing(MMALPortImpl cameraPort, params MMALDownstreamHandlerComponent[] handlerComponents)
        {
            //Enable all connections associated with these components
            foreach (var component in handlerComponents)
            {
                component.EnableConnections();

                foreach (var portNum in component.ProcessingPorts)
                {
                    component.Start(portNum, new Action<MMALBufferImpl, MMALPortBase>(component.ManagedOutputCallback));
                    component.Outputs[portNum].Trigger = new Nito.AsyncEx.AsyncCountdownEvent(1);
                }
                
            }
            
            this.StartCapture(cameraPort);

            //Wait until the process is complete.
            foreach (var component in handlerComponents)
            {
                foreach (var portNum in component.ProcessingPorts)
                {
                    await component.Outputs[portNum].Trigger.WaitAsync();
                }
                
            }
            
            this.StopCapture(cameraPort);

            //Disable the image encoder output port.
            foreach (var component in handlerComponents)
            {
                foreach (var portNum in component.ProcessingPorts)
                {
                    component.Stop(portNum);
                }
                
                //Close open connections.
                component.DisableConnections();

                component.CleanPortPools();
            }
        }
        
        /// <summary>
        /// Disables processing on the camera component
        /// </summary>
        public void DisableCamera()
        {
            this.DownstreamComponents.ForEach(c => c.DisableComponent());
            this.Camera.DisableComponent();
        }

        /// <summary>
        /// Enables processing on the camera component
        /// </summary>
        public void EnableCamera()
        {
            this.DownstreamComponents.ForEach(c => c.EnableComponent());
            this.Camera.EnableComponent();
        }

        /// <summary>
        /// Reconfigures the Camera's still port. This should be called when you change the Still port resolution or encoding/pixel format types.
        /// </summary>
        /// <returns>The camera instance</returns>
        public MMALCamera ReconfigureStill()
        {
            this.DisableCamera();

            this.Connections.Where(c => c.OutputPort == this.Camera.StillPort).ToList()?.ForEach(c => c.Disable());

            this.Camera.InitialiseStill();

            this.Connections.Where(c => c.OutputPort == this.Camera.StillPort).ToList()?.ForEach(c => c.Enable());

            this.EnableCamera();

            return this;
        }

        /// <summary>
        /// Reconfigures the Camera's video port. This should be called when you change the Video port resolution or encoding/pixel format types.
        /// </summary>
        /// <returns>The camera instance</returns>
        public MMALCamera ReconfigureVideo()
        {
            this.DisableCamera();

            this.Connections.Where(c => c.OutputPort == this.Camera.VideoPort).ToList()?.ForEach(c => c.Disable());

            this.Camera.InitialiseVideo();

            this.Connections.Where(c => c.OutputPort == this.Camera.VideoPort).ToList()?.ForEach(c => c.Enable());

            this.EnableCamera();

            return this;
        }

        /// <summary>
        /// Reconfigures the Camera's preview port. This should be called when you change the Video port resolution 
        /// </summary>
        /// <returns>The camera instance</returns>
        public MMALCamera ReconfigurePreview()
        {
            this.DisableCamera();

            this.Connections.Where(c => c.OutputPort == this.Camera.PreviewPort).ToList()?.ForEach(c => c.Disable());
         
            this.Camera.InitialisePreview();
          
            this.Connections.Where(c => c.OutputPort == this.Camera.PreviewPort).ToList()?.ForEach(c => c.Enable());

            this.EnableCamera();

            return this;
        }

        /// <summary>
        /// This applies the configuration settings against the camera such as Saturation, Contrast etc.
        /// </summary>
        /// <returns>The camera instance</returns>
        public MMALCamera ConfigureCameraSettings()
        {
            Debugger.Print("Configuring camera parameters.");

            this.DisableCamera();
            this.Camera.SetCameraParameters();            
            this.EnableCamera();

            return this;
        }
       
        /// <summary>
        /// Helper method to check the Renderer component status. If a Renderer has not been initialized, a warning will
        /// be shown to the user. If a Renderer has been created but a connection has not been initialized, this will be 
        /// done automatically for the user.
        /// </summary>
        private void CheckPreviewComponentStatus()
        {
            //Create connections
            if (!this.Connections.Any(c => c.OutputPort == this.Camera.PreviewPort))
            {
                Helpers.PrintWarning("Preview port does not have a Render component configured. Resulting image will be affected.");
            }
        }

        /// <summary>
        /// Cleans up any unmanaged resources. It is intended for this method to be run when no more activity is to be done on the camera.
        /// </summary>
        public void Cleanup()
        {
            Debugger.Print("Destroying final components");
            
            var tempList = new List<MMALDownstreamComponent>(this.DownstreamComponents);

            tempList.ForEach(c => c.Dispose());
            this.Camera.Dispose();

            BcmHost.bcm_host_deinit();
        }
                 
    }
    
}
