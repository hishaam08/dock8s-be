using System;
using System.IO;
using System.Reflection;
using Docker.DotNet.Models;

namespace Docker.DotNet
{
    public static class MultiplexedStreamExtensions
    {
        /// <summary>
        /// Safely returns the internal writable Stream for MultiplexedStream (compatible with all Docker.DotNet versions).
        /// </summary>
        public static Stream AsStreamForWrite(this MultiplexedStream multiplexedStream)
        {
            if (multiplexedStream == null)
                throw new ArgumentNullException(nameof(multiplexedStream));

            // Try to use reflection to get the private _writeStream field
            var field = typeof(MultiplexedStream).GetField("_writeStream", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var inner = field.GetValue(multiplexedStream) as Stream;
                if (inner != null)
                    return inner;
            }

            // Fallback: create a wrapper that writes via WriteAsync
            return new MultiplexedStreamWriteWrapper(multiplexedStream);
        }

        private class MultiplexedStreamWriteWrapper : Stream
        {
            private readonly MultiplexedStream _stream;
            public MultiplexedStreamWriteWrapper(MultiplexedStream stream) => _stream = stream;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
                => _stream.WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => await _stream.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
