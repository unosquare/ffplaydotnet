using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet
{
    /// <summary>
    /// Performs audio sample decoding, scaling and extraction logic.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaComponent" />
    public sealed unsafe class AudioComponent : MediaComponent
    {
        #region Private Declarations

        /// <summary>
        /// Holds a reference to the audio resampler
        /// This resampler gets disposed upon disposal of this object.
        /// </summary>
        private SwrContext* Scaler = null;

        /// <summary>
        /// The audio samples buffer that has been allocated in unmanaged memory
        /// </summary>
        private IntPtr SamplesBuffer = IntPtr.Zero;

        /// <summary>
        /// The samples buffer length. This might differ from the decompressed,
        /// resampled data length that is obtained in the event. This represents a maximum
        /// allocated length.
        /// </summary>
        private int SamplesBufferLength;

        /// <summary>
        /// Used to determine if we have to reset the scaler parameters
        /// </summary>
        private AudioComponentSpec LastSourceSpec = null;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        internal AudioComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {
            // Placeholder. Nothing else to init.
        }

        #endregion

        #region Methods

        /// <summary>
        /// Allocates a buffer in inmanaged memory only if necessry.
        /// Returns the pointer to the allocated buffer regardless of it being new or existing.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        private IntPtr AllocateBuffer(int length)
        {
            if (SamplesBufferLength < length)
            {
                if (SamplesBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(SamplesBuffer);

                SamplesBufferLength = length;
                SamplesBuffer = Marshal.AllocHGlobal(SamplesBufferLength);
            }

            return SamplesBuffer;
        }

        /// <summary>
        /// Processes the audio frame by resampling it and raising an even if there are any
        /// event subscribers.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            // Compute the timespans
            var renderTime = ffmpeg.av_frame_get_best_effort_timestamp(frame).ToTimeSpan(Stream->time_base);
            var duration = ffmpeg.av_frame_get_pkt_duration(frame).ToTimeSpan(Stream->time_base);

            // Set the state
            LastProcessedTimeUTC = DateTime.UtcNow;
            LastFrameRenderTime = renderTime;

            // Check if there is a handler to feed the conversion to.
            if (Container.HandlesOnAudioDataAvailable == false)
                return;

            // Create the source and target ausio specs. We might need to scale from
            // the source to the target
            var sourceSpec = AudioComponentSpec.CreateSource(frame);
            var targetSpec = AudioComponentSpec.CreateTarget(frame);

            // Initialize or update the audio scaler if required
            if (Scaler == null || LastSourceSpec == null || AudioComponentSpec.AreCompatible(LastSourceSpec, sourceSpec) == false)
            {
                Scaler = ffmpeg.swr_alloc_set_opts(Scaler, targetSpec.ChannelLayout, targetSpec.Format, targetSpec.SampleRate,
                    sourceSpec.ChannelLayout, sourceSpec.Format, sourceSpec.SampleRate, 0, null);

                ffmpeg.swr_init(Scaler);
                LastSourceSpec = sourceSpec;
            }

            // Allocate the unmanaged output buffer
            var outputBuffer = AllocateBuffer(targetSpec.BufferLength);
            var outputBufferPtr = (byte*)outputBuffer;

            // Execute the conversion (audio scaling). It will return the number of samples that were output
            var outputSamplesPerChannel =
                ffmpeg.swr_convert(Scaler, &outputBufferPtr, targetSpec.SamplesPerChannel, frame->extended_data, frame->nb_samples);

            // Compute the buffer length
            var outputBufferLength =
                ffmpeg.av_samples_get_buffer_size(null, targetSpec.ChannelCount, outputSamplesPerChannel, targetSpec.Format, 1);

            // Send data to event subscribers
            Container.RaiseOnAudioDataAvailabe(outputBuffer, outputBufferLength,
                targetSpec.SampleRate, outputSamplesPerChannel, targetSpec.ChannelCount, renderTime, duration);

        }

        /// <summary>
        /// Processes the subtitle frame. This will throw if called.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        /// <exception cref="System.NotSupportedException"></exception>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            throw new NotSupportedException($"{nameof(AudioComponent)} does not support subtitle frame processing.");
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);

            if (Scaler != null)
                fixed (SwrContext** scaler = &Scaler)
                    ffmpeg.swr_free(scaler);

            if (SamplesBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(SamplesBuffer);
        }

        #endregion

    }
}
