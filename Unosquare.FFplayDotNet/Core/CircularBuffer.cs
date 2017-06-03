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

        public ulong ReadCount { get; private set; }

        public ulong WriteCount { get; private set; }

        public byte[] Read(int requestedBytes)
        {
            lock (SyncLock)
            {
                var maxReadLength = Math.Max(WriteCount - ReadCount, 0);
                if ((ulong)requestedBytes > maxReadLength)
                    requestedBytes = (int)maxReadLength;

                var result = new byte[requestedBytes];

                var readCount = 0;
                var readCycles = 0;
                while (readCount < requestedBytes)
                {
                    var copyLength = Math.Min(Length - ReadIndex, requestedBytes - readCount);
                    var sourcePtr = Buffer + ReadIndex;
                    Marshal.Copy(sourcePtr, result, readCount, copyLength);
                    readCount += copyLength;
                    ReadIndex += copyLength;
                    ReadCount += (ulong)copyLength;

                    if (ReadIndex >= Length)
                        ReadIndex = 0;

                    readCycles += 1;
                }

                return result;
            }
            
        }

        public void Write(IntPtr source, int length)
        {
            lock (SyncLock)
            {
                var writeCount = 0;
                while (writeCount < length)
                {
                    var copyLength = Math.Min(Length - WriteIndex, length - writeCount);
                    var sourcePtr = source + writeCount;
                    var targetPtr = Buffer + WriteIndex;
                    Utils.CopyMemory(targetPtr, sourcePtr, (uint)copyLength);

                    writeCount += copyLength;
                    WriteIndex += copyLength;
                    WriteCount += (ulong)copyLength;

                    if (WriteIndex >= Length)
                        WriteIndex = 0;
                }
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
