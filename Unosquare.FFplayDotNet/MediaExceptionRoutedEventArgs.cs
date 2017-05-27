namespace Unosquare.FFplayDotNet
{
    using System;
    using System.Windows;

    /// <summary>
    /// Provides data for the System.Windows.Controls.Image and System.Windows.Controls.MediaElement
    //  failed events.
    /// </summary>
    /// <seealso cref="System.Windows.RoutedEventArgs" />
    public sealed class MediaExceptionRoutedEventArgs : RoutedEventArgs
    {

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaExceptionRoutedEventArgs"/> class.
        /// </summary>
        /// <param name="ex">The ex.</param>
        public MediaExceptionRoutedEventArgs(Exception ex)
        {
            ErrorException = ex;
        }

        /// <summary>
        /// Gets the exception that caused the error condition.
        /// </summary>
        public Exception ErrorException { get; }
    }
}
