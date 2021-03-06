﻿namespace Unosquare.FFplayDotNet
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Threading;

    partial class MediaElement
    {
        #region Helper Methods

        /// <summary>
        /// Creates a new instance of exception routed event arguments.
        /// This method exists because the constructor has not been made public for that class.
        /// </summary>
        /// <param name="routedEvent">The routed event.</param>
        /// <param name="sender">The sender.</param>
        /// <param name="errorException">The error exception.</param>
        /// <returns></returns>
        private static ExceptionRoutedEventArgs CreateExceptionRoutedEventArgs(RoutedEvent routedEvent, object sender, Exception errorException)
        {
            var constructor = (typeof(ExceptionRoutedEventArgs) as TypeInfo).DeclaredConstructors.First();
            return constructor.Invoke(new object[] { routedEvent, sender, errorException }) as ExceptionRoutedEventArgs;
        }

        /// <summary>
        /// Asynchronously invokes the given instructions on the main application dispatcher.
        /// </summary>
        /// <param name="action">The action.</param>
        /// <returns></returns>
        internal void InvokeOnUI(Action action)
        {
            if (Dispatcher == null || Dispatcher.CurrentDispatcher == Dispatcher)
            {
                action();
                return;
            }

            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.Invoke(action, DispatcherPriority.Normal); // Normal is 1 more than DataBind
            }
            catch (TaskCanceledException)
            {
                // swallow
            }
            catch (Exception ex)
            {
                throw ex;
            }

        }

        /// <summary>
        /// Raises the buffering started event.
        /// </summary>
        private void RaiseBufferingStartedEvent()
        {
            InvokeOnUI(() => { RaiseEvent(new RoutedEventArgs(BufferingStartedEvent, this)); });
        }

        /// <summary>
        /// Raises the buffering ended event.
        /// </summary>
        private void RaiseBufferingEndedEvent()
        {
            InvokeOnUI(() => { RaiseEvent(new RoutedEventArgs(BufferingEndedEvent, this)); });
        }

        /// <summary>
        /// Raises the media failed event.
        /// </summary>
        /// <param name="ex">The ex.</param>
        internal void RaiseMediaFailedEvent(Exception ex)
        {
            Container?.Log(MediaLogMessageType.Error, $"Media Failure - {ex?.GetType()}: {ex?.Message}");
            InvokeOnUI(() => { RaiseEvent(CreateExceptionRoutedEventArgs(MediaFailedEvent, this, ex)); });
        }

        /// <summary>
        /// Raises the media opened event.
        /// </summary>
        internal void RaiseMediaOpenedEvent()
        {
            InvokeOnUI(() => { RaiseEvent(new RoutedEventArgs(MediaOpenedEvent, this)); });
        }

        /// <summary>
        /// Raises the media opening event.
        /// </summary>
        internal void RaiseMediaOpeningEvent()
        {
            InvokeOnUI(() =>
            {
                RaiseEvent(new MediaOpeningRoutedEventArgs(MediaOpeningEvent, this, Container.MediaOptions));
            });
        }

        /// <summary>
        /// Raises the media ended event.
        /// </summary>
        private void RaiseMediaEndedEvent()
        {
            InvokeOnUI(() => { RaiseEvent(new RoutedEventArgs(MediaEndedEvent, this)); });
        }

        #endregion

        #region BufferingStarted

        /// <summary>
        /// BufferingStarted is a routed event
        /// </summary>
        public static readonly RoutedEvent BufferingStartedEvent =
            EventManager.RegisterRoutedEvent(
                    nameof(BufferingStarted),
                    RoutingStrategy.Bubble,
                    typeof(RoutedEventHandler),
                    typeof(MediaElement));

        /// <summary>
        /// Occurs when buffering of packets was started
        /// </summary>
        public event RoutedEventHandler BufferingStarted
        {
            add { AddHandler(BufferingStartedEvent, value); }
            remove { RemoveHandler(BufferingStartedEvent, value); }
        }

        #endregion

        #region BufferingEnded

        /// <summary>
        /// BufferingEnded is a routed event
        /// </summary>
        public static readonly RoutedEvent BufferingEndedEvent =
            EventManager.RegisterRoutedEvent(
                    nameof(BufferingEnded),
                    RoutingStrategy.Bubble,
                    typeof(RoutedEventHandler),
                    typeof(MediaElement));

        /// <summary>
        /// Occurs when buffering of packets was Ended
        /// </summary>
        public event RoutedEventHandler BufferingEnded
        {
            add { AddHandler(BufferingEndedEvent, value); }
            remove { RemoveHandler(BufferingEndedEvent, value); }
        }

        #endregion

        #region MediaFailed

        /// <summary>
        /// MediaFailedEvent is a routed event. 
        /// </summary>
        public static readonly RoutedEvent MediaFailedEvent =
            EventManager.RegisterRoutedEvent(
                            nameof(MediaFailed),
                            RoutingStrategy.Bubble,
                            typeof(EventHandler<ExceptionRoutedEventArgs>),
                            typeof(MediaElement));

        /// <summary>
        /// Raised when the media fails to load or a fatal error has occurred which prevents playback.
        /// </summary>
        public event EventHandler<ExceptionRoutedEventArgs> MediaFailed
        {
            add { AddHandler(MediaFailedEvent, value); }
            remove { RemoveHandler(MediaFailedEvent, value); }
        }

        #endregion

        #region MediaOpened

        /// <summary> 
        /// MediaOpened is a routed event.
        /// </summary> 
        public static readonly RoutedEvent MediaOpenedEvent =
            EventManager.RegisterRoutedEvent(
                            nameof(MediaOpened),
                            RoutingStrategy.Bubble,
                            typeof(RoutedEventHandler),
                            typeof(MediaElement));


        /// <summary>
        /// Raised when the media is opened 
        /// </summary> 
        public event RoutedEventHandler MediaOpened
        {
            add { AddHandler(MediaOpenedEvent, value); }
            remove { RemoveHandler(MediaOpenedEvent, value); }
        }

        #endregion

        #region MediaOpening

        /// <summary>
        /// MediaOpeningEvent is a routed event. 
        /// </summary>
        public static readonly RoutedEvent MediaOpeningEvent =
            EventManager.RegisterRoutedEvent(
                            nameof(MediaOpening),
                            RoutingStrategy.Bubble,
                            typeof(EventHandler<MediaOpeningRoutedEventArgs>),
                            typeof(MediaElement));

        /// <summary>
        /// Raised before the input stream of the media is opened.
        /// Use this method to modify the input options.
        /// </summary>
        public event EventHandler<MediaOpeningRoutedEventArgs> MediaOpening
        {
            add { AddHandler(MediaOpeningEvent, value); }
            remove { RemoveHandler(MediaOpeningEvent, value); }
        }

        #endregion

        #region MediaEnded

        /// <summary>
        /// MediaEnded is a routed event 
        /// </summary>
        public static readonly RoutedEvent MediaEndedEvent =
            EventManager.RegisterRoutedEvent(
                            nameof(MediaEnded),
                            RoutingStrategy.Bubble,
                            typeof(RoutedEventHandler),
                            typeof(MediaElement));

        /// <summary> 
        /// Raised when the corresponding media ends.
        /// </summary>
        public event RoutedEventHandler MediaEnded
        {
            add { AddHandler(MediaEndedEvent, value); }
            remove { RemoveHandler(MediaEndedEvent, value); }
        }

        #endregion
    }
}
