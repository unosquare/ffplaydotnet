namespace Unosquare.FFplayDotNet.Sample
{
    using System;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Controls;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Commands

        private DelegateCommand m_OpenCommand = null;
        private DelegateCommand m_PauseCommand = null;
        private DelegateCommand m_PlayCommand = null;
        private DelegateCommand m_StopCommand = null;

        public DelegateCommand OpenCommand
        {
            get
            {
                if (m_OpenCommand == null)
                    m_OpenCommand = new DelegateCommand((a) =>
                    {
                        Media.Source = new Uri(UrlTextBox.Text);
                        window.Title = Media.Source.ToString();
                    }, null);

                return m_OpenCommand;
            }
        }

        public DelegateCommand PauseCommand
        {
            get
            {
                if (m_PauseCommand == null)
                    m_PauseCommand = new DelegateCommand((o) => { Media.Pause(); }, (o) => { return Media.IsPlaying; });

                return m_PauseCommand;
            }
        }

        public DelegateCommand PlayCommand
        {
            get
            {
                if (m_PlayCommand == null)
                    m_PlayCommand = new DelegateCommand((o) => { Media.Play(); }, (o) => { return Media.IsPlaying == false; });

                return m_PlayCommand;
            }
        }

        public DelegateCommand StopCommand
        {
            get
            {
                if (m_StopCommand == null)
                    m_StopCommand = new DelegateCommand((o) => { Media.Stop(); }, (o) =>
                    {
                        return Media.MediaState != MediaState.Close
                            && Media.MediaState != MediaState.Manual;
                    });

                return m_StopCommand;
            }
        }

        #endregion

        public MainWindow()
        {
            // Change the default location of the ffmpeg binaries
            // You can get the binaries here: http://ffmpeg.zeranoe.com/builds/win32/shared/ffmpeg-3.2.4-win32-shared.zip
            Unosquare.FFplayDotNet.MediaElement.FFmpegDirectory = @"C:\ffmpeg";
            //ConsoleManager.ShowConsole();
            InitializeComponent();
            UrlTextBox.Text = TestInputs.YoutubeLocalFile;

            Media.MediaOpening += Media_MediaOpening;
            Media.MediaFailed += Media_MediaFailed;
        }

        private void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            MessageBox.Show($"Media Failed: {e.ErrorException.GetType()}\r\n{e.ErrorException.Message}", 
                "MediaElement Error", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);
        }

        private void Media_MediaOpening(object sender, MediaOpeningRoutedEventArgs e)
        {
            //e.Options.VideoFilter = "yadif"; // "selectivecolor=greens=.5 0 -.33 0:blues=0 .27"; //"scale=101:-1,noise=alls=20:allf=t+u";
            //e.Options.IsAudioDisabled = true;
            e.Options.LogMessageCallback = new Action<MediaLogMessageType, string>((t, m) =>
            {
                if (t == MediaLogMessageType.Trace) return;

                Debug.WriteLine($"{t} - {m}");
                //Terminal.Log(m, nameof(MediaElement), (LogMessageType)t);
            });
        }
    }
}
