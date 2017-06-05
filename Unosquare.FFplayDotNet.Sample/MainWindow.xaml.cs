namespace Unosquare.FFplayDotNet.Sample
{
    using Swan;
    using System;
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
            ConsoleManager.ShowConsole();
            InitializeComponent();
            UrlTextBox.Text = TestInputs.YoutubeLocalFile;
            Media.MediaOpening += Media_MediaOpening;
        }

        private void Media_MediaOpening(object sender, MediaOpeningRoutedEventArgs e)
        {
            e.Options.LogMessageCallback = new Action<MediaLogMessageType, string>((t, m) =>
            {
                Terminal.Log(m, nameof(MediaElement), (LogMessageType)t);
            });
        }
    }
}
