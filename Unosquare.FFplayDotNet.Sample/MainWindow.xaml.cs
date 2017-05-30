namespace Unosquare.FFplayDotNet.Sample
{
    using Swan;
    using System;
    using System.Windows;



    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private DelegateCommand m_OpenCommand = null;
        public DelegateCommand OpenCommand
        {
            get
            {
                if (m_OpenCommand == null)
                {
                    m_OpenCommand = new DelegateCommand((a) =>
                    {
                        Media.Source = new Uri(TestInputs.YoutubeLocalFile);
                        window.Title = Media.Source.ToString();
                        Media.Play();
                    }, null);
                }

                return m_OpenCommand;
            }
        }

        public MainWindow()
        {
            ConsoleManager.ShowConsole();
            InitializeComponent();
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
