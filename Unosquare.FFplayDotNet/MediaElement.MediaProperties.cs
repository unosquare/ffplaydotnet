namespace Unosquare.FFplayDotNet
{
    using Core;
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Threading;

    partial class MediaElement
    {

        #region Property Backing

        private bool m_HasMediaEnded = false;
        private double m_BufferingProgress = 0;
        private double m_DownloadProgress = 0;
        private bool m_IsBuffering = false;
        private MediaState m_MediaState = MediaState.Close;

        #endregion

        #region Notification Properties

        /// <summary> 
        /// Returns whether the given media has audio. 
        /// Only valid after the MediaOpened event has fired.
        /// </summary> 
        public bool HasAudio { get { return Container == null ? false : Container.Components.HasAudio; } }

        /// <summary> 
        /// Returns whether the given media has video. Only valid after the
        /// MediaOpened event has fired.
        /// </summary>
        public bool HasVideo { get { return Container?.Components.HasVideo ?? false; } }

        /// <summary>
        /// Gets the video codec.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public string VideoCodec { get { return Container?.Components?.Video?.Codec; } }

        /// <summary>
        /// Gets the video bitrate.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public int VideoBitrate { get { return Container?.Components?.Video?.Bitrate ?? 0; } }

        /// <summary>
        /// Returns the natural width of the media in the video.
        /// Only valid after the MediaOpened event has fired.
        /// </summary> 
        public int NaturalVideoWidth { get { return Container?.Components?.Video?.FrameWidth ?? 0; } }

        /// <summary> 
        /// Returns the natural height of the media in the video.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public int NaturalVideoHeight { get { return Container?.Components.Video?.FrameHeight ?? 0; } }

        /// <summary>
        /// Gets the video frame rate.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public double VideoFrameRate { get { return Container?.Components.Video?.BaseFrameRate ?? 0; } }

        /// <summary>
        /// Gets the duration in seconds of the video frame.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public double VideoFrameLength { get { return 1d / (Container?.Components?.Video?.BaseFrameRate ?? 0); } }

        /// <summary>
        /// Gets the audio codec.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public string AudioCodec { get { return Container?.Components?.Audio?.Codec; } }

        /// <summary>
        /// Gets the audio bitrate.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public int AudioBitrate { get { return Container?.Components?.Audio?.Bitrate ?? 0; } }

        /// <summary>
        /// Gets the audio channels count.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public int AudioChannels { get { return Container?.Components?.Audio?.Channels ?? 0; } }

        /// <summary>
        /// Gets the audio sample rate.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public int AudioSampleRate { get { return Container?.Components?.Audio?.SampleRate ?? 0; } }

        /// <summary>
        /// Gets the audio bits per sample.
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public int AudioBitsPerSample { get { return Container?.Components?.Audio?.BitsPerSample ?? 0; } }

        /// <summary>
        /// Gets the Media's natural duration
        /// Only valid after the MediaOpened event has fired.
        /// </summary>
        public Duration NaturalDuration
        {
            get
            {
                return Container == null ? Duration.Automatic :
                    Container.MediaDuration == TimeSpan.MinValue ?
                        Duration.Forever :
                            new Duration(Container.MediaDuration);
            }
        }

        /// <summary>
        /// Returns whether the given media can be paused. 
        /// This is only valid after the MediaOpened event has fired.
        /// Note: This property is computed based on wether the stream is detected to be a live stream.
        /// </summary>
        public bool CanPause { get { return Container != null ? Container.IsStreamRealtime == false : true; } }

        /// <summary>
        /// Gets a value indicating whether the media is playing.
        /// </summary>
        public bool IsPlaying { get { return MediaState == MediaState.Play; } }

        /// <summary>
        /// Gets a value indicating whether the media has reached its end.
        /// </summary>
        public bool HasMediaEnded
        {
            get { return m_HasMediaEnded; }
            private set { SetProperty(ref m_HasMediaEnded, value); }
        }

        /// <summary>
        /// Gets a value that indicates the percentage of buffering progress made.
        /// Range is from 0 to 1
        /// </summary>
        public double BufferingProgress
        {
            get { return m_BufferingProgress; }
            private set { SetProperty(ref m_BufferingProgress, value); }
        }

        /// <summary>
        /// Gets a value that indicates the percentage of download progress made.
        /// Range is from 0 to 1
        /// </summary>
        public double DownloadProgress
        {
            get { return m_DownloadProgress; }
            private set { SetProperty(ref m_DownloadProgress, value); }
        }

        /// <summary>
        /// Get a value indicating whether the media is buffering.
        /// </summary>
        public bool IsBuffering
        {
            get { return m_IsBuffering; }
            private set { SetProperty(ref m_IsBuffering, value); }
        }

        public MediaState MediaState
        {
            get { return m_MediaState; }
            private set
            {
                SetProperty(ref m_MediaState, value);
                OnPropertyChanged(nameof(IsPlaying));
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Updates the media properties.
        /// </summary>
        private void UpdateMediaProperties()
        {
            OnPropertyChanged(nameof(HasAudio));
            OnPropertyChanged(nameof(HasVideo));
            OnPropertyChanged(nameof(VideoCodec));
            OnPropertyChanged(nameof(VideoBitrate));
            OnPropertyChanged(nameof(NaturalVideoWidth));
            OnPropertyChanged(nameof(NaturalVideoHeight));
            OnPropertyChanged(nameof(VideoFrameRate));
            OnPropertyChanged(nameof(VideoFrameLength));
            OnPropertyChanged(nameof(AudioCodec));
            OnPropertyChanged(nameof(AudioBitrate));
            OnPropertyChanged(nameof(AudioChannels));
            OnPropertyChanged(nameof(AudioSampleRate));
            OnPropertyChanged(nameof(AudioBitsPerSample));
            OnPropertyChanged(nameof(NaturalDuration));

            if (Container == null)
            {
                Volume = 0;
                Position = TimeSpan.Zero;
            }
            else
            {
                //Volume = System.Convert.ToDouble(Media.Volume);
                //Position = Media.Position;
            }

            DownloadProgress = 0;
            BufferingProgress = 0;
            IsBuffering = false;
            IsMuted = false;
            HasMediaEnded = false;
            SpeedRatio = Constants.DefaultSpeedRatio;
        }

        #endregion
    }
}
