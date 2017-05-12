namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;
    using Unosquare.FFplayDotNet.Core;

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
        /// Used to determine if we have to reset the scaler parameters
        /// </summary>
        private AudioParams LastSourceSpec = null;

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

        protected override unsafe FrameSource CreateFrame(AVFrame* frame)
        {
            var frameHolder = new AudioFrameSource(frame, Stream->time_base);
            return frameHolder;
        }

        internal override void Materialize(FrameSource input, Frame output)
        {
            var source = input as AudioFrameSource;
            var target = output as AudioFrame;

            if (source == null || target == null)
                throw new ArgumentNullException($"{nameof(input)} and {nameof(output)} are either null or not of a compatible media type '{MediaType}'");

            // Create the source and target ausio specs. We might need to scale from
            // the source to the target
            var sourceSpec = AudioParams.CreateSource(source.Pointer);
            var targetSpec = AudioParams.CreateTarget(source.Pointer);

            // Initialize or update the audio scaler if required
            if (Scaler == null || LastSourceSpec == null || AudioParams.AreCompatible(LastSourceSpec, sourceSpec) == false)
            {
                Scaler = ffmpeg.swr_alloc_set_opts(Scaler, targetSpec.ChannelLayout, targetSpec.Format, targetSpec.SampleRate,
                    sourceSpec.ChannelLayout, sourceSpec.Format, sourceSpec.SampleRate, 0, null);

                ffmpeg.swr_init(Scaler);
                LastSourceSpec = sourceSpec;
            }

            // Allocate the unmanaged output buffer
            if (target.AudioBufferLength < targetSpec.BufferLength)
            {
                if (target.AudioBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(target.AudioBuffer);

                target.AudioBufferLength = targetSpec.BufferLength;
                target.AudioBuffer = Marshal.AllocHGlobal(targetSpec.BufferLength);
            }

            var outputBufferPtr = (byte*)target.AudioBuffer;

            // Execute the conversion (audio scaling). It will return the number of samples that were output
            var outputSamplesPerChannel =
                ffmpeg.swr_convert(Scaler, &outputBufferPtr, targetSpec.SamplesPerChannel,
                    source.Pointer->extended_data, source.Pointer->nb_samples);

            // Compute the buffer length
            var outputBufferLength =
                ffmpeg.av_samples_get_buffer_size(null, targetSpec.ChannelCount, outputSamplesPerChannel, targetSpec.Format, 1);

            // set the target properties
            target.BufferLength = outputBufferLength;
            target.ChannelCount = targetSpec.ChannelCount;
            target.Duration = source.Duration;
            target.EndTime = source.EndTime;
            target.SampleRate = targetSpec.SampleRate;
            target.SamplesPerChannel = outputSamplesPerChannel;
            target.StartTime = source.StartTime;

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
        }

        #endregion

    }
}
