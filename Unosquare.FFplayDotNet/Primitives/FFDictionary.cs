using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Unosquare.FFplayDotNet.Core;

namespace Unosquare.FFplayDotNet.Primitives
{
    /// <summary>
    /// An AVDictionary and DictionaryEntry wrapper
    /// </summary>
    internal unsafe class FFDictionary : IDisposable
    {
        private AVDictionary* Dictionary;
        //internal readonly GCHandle DictionaryHandle;
        //internal readonly GCHandle DictionaryHandleReference;

        public FFDictionary()
        {
            //Dictionary = new AVDictionary();
            //DictionaryHandle = GCHandle.Alloc(Dictionary, GCHandleType.Pinned);
            //DictionaryHandleReference = GCHandle.Alloc(DictionaryHandle, GCHandleType.Pinned);
        }

        /// <summary>
        /// Fills this dictionary with a set of options
        /// </summary>
        /// <param name="other"></param>
        public void Fill(Dictionary<string, string> other)
        {
            if (other == null) return;
            foreach (var kvp in other)
            {
                this[kvp.Key] = kvp.Value;
            }
        }

        public AVDictionary* Pointer { get { return Dictionary; } }

        public AVDictionary** Reference { get; private set; }

        public bool KeyExists(string key, bool matchCase = true)
        {
            return ffmpeg.av_dict_get(Pointer, key, null, matchCase ? ffmpeg.AV_DICT_MATCH_CASE : 0) != null;
        }

        public string this[string key]
        {
            get
            {
                return Get(key);
            }
            set
            {
                Set(key, value, false);
            }
        }

        public void Set(string key, string value)
        {
            Set(key, value, false);
        }

        public void Set(string key, string value, bool dontOverwrite)
        {
            var flags = 0;
            if (dontOverwrite) flags |= ffmpeg.AV_DICT_DONT_OVERWRITE;

            if (Dictionary == null)
            {
                AVDictionary* newDict = null;
                Reference = &newDict;
            }

            ffmpeg.av_dict_set(Reference, key, value, flags);
            Dictionary = *Reference;
        }

        public void Remove(string key)
        {
            if (KeyExists(key))
                Set(key, null, false);
        }

        public void AppendValue(string key, string appendedValue)
        {
            if (Dictionary == null)
            {
                AVDictionary* newDict = null;
                Reference = &newDict;
            }

            ffmpeg.av_dict_set(Reference, key, appendedValue, ffmpeg.AV_DICT_APPEND);
            Dictionary = *Reference;
        }

        public static FFDictionaryEntry GetEntry(AVDictionary* dictionary, string key, bool matchCase = true)
        {
            var entryPointer = ffmpeg.av_dict_get(dictionary, key, null, matchCase ? ffmpeg.AV_DICT_MATCH_CASE : 0);
            if (entryPointer == null) return null;
            return new FFDictionaryEntry(entryPointer);
        }

        public FFDictionaryEntry GetEntry(string key, bool matchCase = true)
        {
            return GetEntry(Pointer, key, matchCase);
        }

        public FFDictionaryEntry First()
        {
            return Next(null);
        }

        public FFDictionaryEntry Next(FFDictionaryEntry prior)
        {
            var priorEntry = prior == null ? null : prior.Pointer;
            var nextEntry = ffmpeg.av_dict_get(Pointer, "", priorEntry, ffmpeg.AV_DICT_IGNORE_SUFFIX);
            return new FFDictionaryEntry(nextEntry);
        }

        public string Get(string key)
        {
            var entry = GetEntry(key, true);
            return entry == null ? null : entry.Value;
        }

        #region IDisposable Support

        /// <summary>
        /// To detect redundant Dispose calls
        /// </summary>
        private bool IsDisposing = false;

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposing)
            {
                if (alsoManaged)
                {
                    ffmpeg.av_dict_free(Reference);
                }

                IsDisposing = true;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion


    }

    internal unsafe class FFDictionaryEntry
    {
        internal readonly AVDictionaryEntry* Pointer;

        public FFDictionaryEntry(AVDictionaryEntry* entryPointer)
        {
            Pointer = entryPointer;
        }

        public string Key
        {
            get
            {
                return Pointer != null ?
                    Native.BytePtrToString(Pointer->key) : null;
            }
        }
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
