namespace Unosquare.FFplayDotNet
{
    using Core;
    using Decoding;
    using Rendering;
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Controls;

    partial class MediaElement
    {

        #region Private Members

        /// <summary>
        /// The underlying media container that provides access to 
        /// individual media component streams
        /// </summary>
        internal MediaContainer Container = null;

        /// <summary>
        /// Represents a real-time time measuring device.
        /// Rendering media should occur as requested by the clock.
        /// </summary>
        private readonly Clock Clock = new Clock();

        #endregion

        #region Private Methods

        /// <summary>
        /// Creates a new instance of the renderer of the given type.
        /// </summary>
        /// <param name="mediaType">Type of the media.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentException"></exception>
        private IRenderer CreateRenderer(MediaType mediaType)
        {
            if (mediaType == MediaType.Audio) return new AudioRenderer(this);
            else if (mediaType == MediaType.Video) return new VideoRenderer(this);
            else if (mediaType == MediaType.Subtitle) return new SubtitleRenderer(this);

            throw new ArgumentException($"No suitable renderer for Media Type '{mediaType}'");
        }

        /// <summary>
        /// Performs a seek operation to the specified position.
        /// </summary>
        /// <param name="position">The position.</param>
        private void Seek(TimeSpan position)
        {
            SeekingDone.Wait();
            var startTime = DateTime.UtcNow;
            var resumeClock = Clock.IsRunning;
            Clock.Pause();

            SeekingDone.Reset();
            PacketReadingCycle.Wait();
            FrameDecodingCycle.Wait();
            BlockRenderingCycle.Wait();

            // Clear Blocks and frames, reset the render times
            foreach (var t in Container.Components.MediaTypes)
            {
                Frames[t].Clear();
                Blocks[t].Clear();
                LastRenderTime[t] = TimeSpan.MinValue;
            }

            // Populate frame with after-seek operation
            var frames = Container.Seek(position);
            foreach (var frame in frames)
                Frames[frame.MediaType].Push(frame);

            // Resume the clock if it was running before the seek operation
            OnPropertyChanged(nameof(Position));
            if (resumeClock)
                Clock.Play();

            Container.Log(MediaLogMessageType.Debug,
                $"SEEK D: Elapsed: {startTime.DebugElapsedUtc()}");

            RequestedSeekPosition = null;
            SeekingDone.Set();
        }

        #endregion

        #region Open and Close

        /// <summary>
        /// Opens the specified media Asynchronously
        /// </summary>
        /// <param name="uri">The URI.</param>
        /// <returns></returns>
        private async Task OpenAsync(Uri uri)
        {
            try
            {
                // Register FFmpeg if not already done
                if (IsFFmpegLoaded == false)
                    FFmpegDirectory = Utils.RegisterFFmpeg(FFmpegDirectory);

                IsFFmpegLoaded = true;

                await Task.Run(() =>
                {
                    var mediaUrl = uri.IsFile ? uri.LocalPath : uri.ToString();

                    Container = new MediaContainer(mediaUrl);
                    RaiseMediaOpeningEvent();
                    Container.Log(MediaLogMessageType.Debug, $"{nameof(OpenAsync)}: Entered");
                    Container.Initialize();
                });

                foreach (var t in Container.Components.MediaTypes)
                {
                    Blocks[t] = new MediaBlockBuffer(MaxBlocks[t], t);
                    Frames[t] = new MediaFrameQueue();
                    LastRenderTime[t] = TimeSpan.MinValue;
                    Renderers[t] = CreateRenderer(t);
                }

                IsTaskCancellationPending = false;

                BlockRenderingCycle.Set();
                FrameDecodingCycle.Set();
                PacketReadingCycle.Set();

                PacketReadingTask = new Thread(RunPacketReadingWorker) { IsBackground = true };
                FrameDecodingTask = new Thread(RunFrameDecodingWorker) { IsBackground = true };
                BlockRenderingTask = new Thread(RunBlockRenderingWorker) { IsBackground = true };

                PacketReadingTask.Start();
                FrameDecodingTask.Start();
                BlockRenderingTask.Start();

                RaiseMediaOpenedEvent();

                if (LoadedBehavior == MediaState.Play)
                    Play();
            }
            catch (Exception ex)
            {
                RaiseMediaFailedEvent(ex);
            }
            finally
            {
                UpdateMediaProperties();
                Container?.Log(MediaLogMessageType.Debug, $"{nameof(OpenAsync)}: Completed");
            }
        }

        public async Task CloseAsync()
        {
            Container?.Log(MediaLogMessageType.Debug, $"{nameof(CloseAsync)}: Entered");
            Clock.Pause();
            UpdatePosition(TimeSpan.Zero);

            IsTaskCancellationPending = true;

            // Wait for cycles to complete.
            await Task.Run(() =>
            {
                while (!BlockRenderingCycle.Wait(1)) { }
                while (!FrameDecodingCycle.Wait(1)) { }
                while (!PacketReadingCycle.Wait(1)) { }
            });

            BlockRenderingTask?.Join();
            FrameDecodingTask?.Join();
            PacketReadingTask?.Join();

            BlockRenderingTask = null;
            FrameDecodingTask = null;
            PacketReadingTask = null;

            foreach (var renderer in Renderers.Values)
                renderer.Close();

            Renderers.Clear();

            // Reset the clock
            Clock.Reset();

            Container?.Log(MediaLogMessageType.Debug, $"{nameof(CloseAsync)}: Completed");

            // Dispose the container
            if (Container != null)
            {
                Container.Dispose();
                Container = null;
            }

            // Dispose the Blocks for all components
            foreach (var kvp in Blocks) kvp.Value.Dispose();
            Blocks.Clear();

            // Dispose the Frames for all components
            foreach (var kvp in Frames) kvp.Value.Dispose();
            Frames.Clear();

            // Clear the render times
            LastRenderTime.Clear();

            // Update notification properties
            UpdateMediaProperties();
            MediaState = MediaState.Close;
        }

        #endregion

        #region Public API

        public void Play()
        {
            foreach (var renderer in Renderers.Values)
                renderer.Play();
            Clock.Play();
            MediaState = MediaState.Play;
        }

        public void Pause()
        {
            foreach (var renderer in Renderers.Values)
                renderer.Pause();
            Clock.Pause();
            MediaState = MediaState.Pause;
        }

        public void Stop()
        {
            foreach (var renderer in Renderers.Values)
                renderer.Stop();
            Clock.Reset();
            Seek(TimeSpan.Zero);
        }

        #endregion

    }
}
