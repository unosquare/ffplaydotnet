namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;
    using Unosquare.FFplayDotNet.Core;

    /// <summary>
    /// An AVDictionaryEntry wrapper
    /// </summary>
    internal unsafe class FFDictionaryEntry
    {
        // This ointer is generated in unmanaged code.
        internal readonly AVDictionaryEntry* Pointer;

        /// <summary>
        /// Initializes a new instance of the <see cref="FFDictionaryEntry"/> class.
        /// </summary>
        /// <param name="entryPointer">The entry pointer.</param>
        public FFDictionaryEntry(AVDictionaryEntry* entryPointer)
        {
            Pointer = entryPointer;
        }

        /// <summary>
        /// Gets the key.
        /// </summary>
        public string Key
        {
            get
            {
                return Pointer != null ?
                    Native.BytePtrToString(Pointer->key) : null;
            }
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public string Value
        {
            get
            {
                return Pointer != null ?
                    Native.BytePtrToString(Pointer->value) : null;
            }
        }
    }
}
