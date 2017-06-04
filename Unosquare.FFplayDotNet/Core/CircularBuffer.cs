namespace Unosquare.FFplayDotNet.Core
{
    using System;
    using System.Runtime.InteropServices;

    internal sealed class CircularBuffer : IDisposable
    {

        private IntPtr Buffer = IntPtr.Zero;
        private readonly object SyncLock = new object();

        public CircularBuffer(int bufferLength)
        {
            Length = bufferLength;
            Buffer = Marshal.AllocHGlobal(Length);
        }

        public int Length { get; private set; }

        public int ReadIndex { get; private set; }

        public int WriteIndex { get; private set; }

        public TimeSpan WriteTag { get; private set; } = TimeSpan.MinValue;

        /// <summary>
        /// Gets the available bytes to read.
        /// </summary>
        public int Available { get; private set; }

        public byte[] Read(int requestedBytes)
        {
            lock (SyncLock)
            {
                if (requestedBytes > Available)
                    throw new InvalidOperationException(
                        $"Unable to read {requestedBytes} bytes. Only {Available} bytes are available");

                var result = new byte[requestedBytes];

                var readCount = 0;
                while (readCount < requestedBytes)
                {
                    var copyLength = Math.Min(Length - ReadIndex, requestedBytes - readCount);
                    var sourcePtr = Buffer + ReadIndex;
                    Marshal.Copy(sourcePtr, result, readCount, copyLength);

                    readCount += copyLength;
                    ReadIndex += copyLength;
                    Available -= copyLength;

                    if (ReadIndex >= Length)
                        ReadIndex = 0;
                }

                return result;
            }

        }

        public void Write(IntPtr source, int length, TimeSpan writeTag)
        {
            lock (SyncLock)
            {
                if (Available + length > Length)
                    throw new InvalidOperationException(
                        $"Unable to write to circular buffer. Call the {nameof(Read)} method to make some additional room");

                var writeCount = 0;
                while (writeCount < length)
                {
                    var copyLength = Math.Min(Length - WriteIndex, length - writeCount);
                    var sourcePtr = source + writeCount;
                    var targetPtr = Buffer + WriteIndex;
                    Utils.CopyMemory(targetPtr, sourcePtr, (uint)copyLength);

                    writeCount += copyLength;
                    WriteIndex += copyLength;
                    Available += copyLength;

                    if (WriteIndex >= Length)
                        WriteIndex = 0;
                }

                WriteTag = writeTag;
            }
        }

        public void Dispose()
        {
            if (Buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Buffer);
                Buffer = IntPtr.Zero;
                Length = 0;
            }
        }
    }
}
