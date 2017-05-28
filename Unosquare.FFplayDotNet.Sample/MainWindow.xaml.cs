namespace Unosquare.FFplayDotNet.Sample
{
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
            Media.MediaOpening += Media_MediaOpening;
            Media.Source = new Uri(TestInputs.HlsStream);
            Media.Play();            
        }

        private void Media_MediaOpening(object sender, MediaOpeningRoutedEventArgs e)
        {
            
            //throw new NotImplementedException();
        }
    }
}
