namespace Unosquare.FFplayDotNet.Core
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Miscellaneous native methods
    /// </summary>
    internal unsafe static class Native
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32")]
        public static extern void RtlMoveMemory(IntPtr destination, IntPtr source, uint length);

        [DllImport("kernel32")]
        public static extern void RtlMoveMemory(byte* destination, byte* source, uint length);

        [DllImport("kernel32")]
        public static extern void RtlCopyMemory(IntPtr destination, IntPtr source, uint length);

        [DllImport("kernel32")]
        public static extern void RtlCopyMemory(byte* destination, byte* source, uint length);

        [DllImport("kernel32")]
        public static extern void RtlFillMemory(IntPtr destination, uint len, byte fill);

        [DllImport("kernel32")]
        public static extern void RtlFillMemory(byte* destination, uint length, byte fill);

        /// <summary>
        /// Converts a byte pointer to a string
        /// </summary>
        /// <param name="bytePtr">The byte PTR.</param>
        /// <returns></returns>
        public static string BytePtrToString(byte* bytePtr)
        {
            return Marshal.PtrToStringAnsi(new IntPtr(bytePtr));
        }

        /// <summary>
        /// Fill a block of memory.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="arr">The arr.</param>
        /// <param name="value">The value.</param>
        /// <param name="repeat">The repeat.</param>
        public static void memset<T>(Array arr, T value, int repeat)
        {
            var typedArray = arr as T[];
            for (var i = 0; i < repeat; i++)
                typedArray[i] = value;
        }

        /// <summary>
        /// Fill a block of memory
        /// </summary>
        /// <param name="destination">The arr.</param>
        /// <param name="value">The value.</param>
        /// <param name="length">The repeat.</param>
        public static void memset(byte* destination, byte value, int length)
        {
            if (Helper.IsWindows)
            {
                RtlFillMemory(destination, (uint)length, value);
                return;
            }

            byte* pArr = destination;

            // Copy the specified number of bytes from source to target.
            for (int i = 0; i < length; i++)
            {
                *pArr = value;
                pArr++;
            }
        }

        /// <summary>
        /// Copies a block of memory
        /// </summary>
        /// <param name="destination">The target.</param>
        /// <param name="source">The source.</param>
        /// <param name="length">The length.</param>
        public static void memcpy(byte* destination, byte* source, int length)
        {

            if (Helper.IsWindows)
            {
                RtlCopyMemory(destination, source, (uint)length);
                return;
            }

            // Set the starting points in source and target for the copying.
            byte* sourcePtr = source;
            byte* targetPtr = destination;

            // Copy the specified number of bytes from source to target.
            for (int i = 0; i < length; i++)
            {
                *targetPtr = *sourcePtr;
                targetPtr++;
                sourcePtr++;
            }
        }
    }
}
