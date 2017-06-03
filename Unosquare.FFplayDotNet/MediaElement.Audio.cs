namespace Unosquare.FFplayDotNet
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using NAudio.Wave;
    using NAudio.CoreAudioApi;
    using Core;
    using System.Threading;
    using Decoding;
    using System.Runtime.InteropServices;

    partial class MediaElement
    {
        private DirectSoundOut AudioDevice;
        private VolumeWaveProvider16 AudioSamplesProvider;
        private object AudioLock = new object();
        private int CurrentAudioBlockIndex = 0;
        private int CurrentAudioBlockOffset = 0;

        private void InitializeAudio()
        {
            if (AudioDevice != null)
                DestroyAudio();
            
            if (AudioSamplesProvider == null)
                AudioSamplesProvider = new VolumeWaveProvider16(new CallbackWaveProvider16(RenderAudioBufferCallback));

            AudioDevice = new DirectSoundOut();
            AudioDevice.Init(AudioSamplesProvider);
        }

        private void DestroyAudio()
        {
            if (AudioDevice == null) return;
            AudioDevice.Stop();
            AudioDevice.Dispose();
            AudioDevice = null;
        }

        private void PlayAudio()
        {
            if (AudioDevice == null)
                InitializeAudio();

            AudioDevice.Play();
        }

        private void PauseAudio()
        {
            if (AudioDevice == null)
                InitializeAudio();

            AudioDevice.Pause();
        }

        private void StopAudio()
        {
            if (AudioDevice == null)
                InitializeAudio();

            AudioDevice.Stop();
        }

        private byte[] RenderAudioBufferCallback(int requestedBytes)
        {

            if (IsPlaying == false || HasAudio == false)
                return null;


            lock (AudioLock)
            {
                var resultPtr = Marshal.AllocHGlobal(requestedBytes);
                var writtenCount = 0;


                while (writtenCount < requestedBytes)
                {
                    if (CurrentAudioBlockIndex < 0 || CurrentAudioBlockIndex >= Blocks[MediaType.Audio].Count)
                        break;

                    var currentAudioBlock = Blocks[MediaType.Audio][CurrentAudioBlockIndex] as AudioBlock;
                    var availableBytes = currentAudioBlock.BufferLength - CurrentAudioBlockOffset;
                    if (availableBytes <= 0)
                    {
                        CurrentAudioBlockIndex += 1;
                        CurrentAudioBlockOffset = 0;
                        continue;
                    }

                    var copyLength = Math.Min(availableBytes, requestedBytes - writtenCount);
                    var sourcePtr = currentAudioBlock.Buffer + CurrentAudioBlockOffset;
                    var targetPtr = resultPtr + writtenCount;

                    Utils.CopyMemory(targetPtr, sourcePtr, (uint)copyLength);
                    CurrentAudioBlockOffset += copyLength;
                    writtenCount += copyLength;
                }

                var result = new byte[requestedBytes];
                Marshal.Copy(resultPtr, result, 0, result.Length);
                Marshal.FreeHGlobal(resultPtr);

                return result;

            }

        }

    }

    internal class CallbackWaveProvider16 : IWaveProvider
    {
        public delegate byte[] RenderAudioBufferDelegate(int wantedBytes);

        private RenderAudioBufferDelegate RenderCallback = null;
        private WaveFormat m_Format = null;
        private byte[] SilenceBuffer = null;

        public CallbackWaveProvider16(RenderAudioBufferDelegate renderCallback)
        {
            m_Format = new WaveFormat(AudioParams.Output.SampleRate, 16, AudioParams.Output.ChannelCount);
            SilenceBuffer = new byte[m_Format.BitsPerSample / 8 * m_Format.Channels * 2];
            RenderCallback = renderCallback;
        }

        /// <summary>
        /// Fill the specified buffer with wave data.
        /// </summary>
        /// <param name="buffer">The buffer to fill of wave data.</param>
        /// <param name="offset">Offset into buffer</param>
        /// <param name="count">The number of bytes to read</param>
        /// <returns>
        /// the number of bytes written to the buffer.
        /// </returns>
        public int Read(byte[] buffer, int offset, int count)
        {
            byte[] renderBuffer = null;

            try { renderBuffer = RenderCallback(count); }
            catch { }
            if (renderBuffer == null) renderBuffer = new byte[] { };

            if (renderBuffer.Length == 0)
            {
                Buffer.BlockCopy(SilenceBuffer, 0, buffer, offset, Math.Min(SilenceBuffer.Length, buffer.Length));
                return SilenceBuffer.Length;
            }
            else
            {
                Buffer.BlockCopy(renderBuffer, 0, buffer, offset, renderBuffer.Length);
                return renderBuffer.Length;
            }

        }

        public WaveFormat WaveFormat
        {
            get { return m_Format; }
        }

    }

}
