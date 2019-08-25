﻿using System.IO;
using MMALSharp.Common.Utility;

namespace MMALSharp.Handlers
{
    public class InputCaptureHandler : IInputCaptureHandler
    {
        /// <summary>
        /// Gets or sets the stream to retrieve input data from.
        /// </summary>
        public Stream CurrentStream { get; }

        private int Processed { get; set; }

        /// <summary>
        /// Creates a new instance of the <see cref="InputCaptureHandler"/> class with the specified input stream, output directory and output filename extension.
        /// </summary>
        /// <param name="inputStream">The stream to retrieve input data from.</param>
        public InputCaptureHandler(Stream inputStream)
        {
            this.CurrentStream = inputStream;
        }

        /// <summary>
        /// When overridden in a derived class, returns user provided image data.
        /// </summary>
        /// <param name="allocSize">The count of bytes to return at most in the <see cref="ProcessResult"/>.</param>
        /// <returns>A <see cref="ProcessResult"/> object containing the user provided image data.</returns>
        public virtual ProcessResult Process(uint allocSize)
        {
            var buffer = new byte[allocSize];

            var read = this.CurrentStream.Read(buffer, 0, (int)allocSize);

            this.Processed += read;

            if (read < allocSize)
            {
                return new ProcessResult { Success = true, BufferFeed = buffer, EOF = true, DataLength = read };
            }

            return new ProcessResult { Success = true, BufferFeed = buffer, DataLength = read };
        }

        public void Dispose()
        {
            this.CurrentStream?.Dispose();
        }

        public string TotalProcessed()
        {
            return $"{Helpers.ConvertBytesToMegabytes(this.Processed)}";
        }
    }
}