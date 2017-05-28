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
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Native.AllocConsole();
            Media.MediaOpening += Media_MediaOpening;
            Media.Source = new Uri(TestInputs.HlsStream);
            Media.Play();            
        }

        private void Media_MediaOpening(object sender, MediaOpeningRoutedEventArgs e)
        {
            e.Options.LogMessageCallback = new Action<MediaLogMessageType, string>((t, m) => {
                Terminal.Log(m, nameof(MediaElement), (LogMessageType)t);
            });
        }
    }
}
