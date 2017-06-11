namespace Unosquare.FFplayDotNet.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    internal sealed class MediaCommandManager
    {
        #region Private Declarations

        private readonly object SyncLock = new object();
        private readonly List<MediaCommand> Commands = new List<MediaCommand>();
        private readonly MediaElement MediaElement;
        private MediaCommand m_ExecutingCommand = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaCommandManager"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        public MediaCommandManager(MediaElement mediaElement)
        {
            MediaElement = mediaElement;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of commands pending execution.
        /// </summary>
        public int PendingCount { get { lock (SyncLock) return Commands.Count; } }

        /// <summary>
        /// Gets or sets the currently executing command.
        /// If there are no commands being executed, then it returns null;
        /// </summary>
        public MediaCommand ExecutingCommand
        {
            get { lock (SyncLock) { return m_ExecutingCommand; } }
            set { lock (SyncLock) { m_ExecutingCommand = value; } }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Opens the specified URI.
        /// </summary>
        /// <param name="uri">The URI.</param>
        /// <returns></returns>
        public Task Open(Uri uri)
        {
            lock (SyncLock)
            {
                Commands.Clear();
                var command = new OpenCommand(MediaElement, uri);
                var task = command.ExecuteAsync();
                return task;
            }
        }

        public Task Play()
        {
            lock (SyncLock)
            {
                var command = new PlayCommand(MediaElement);
                Commands.Add(command);
                return command.Promise;
            }
        }

        public Task Pause()
        {
            lock (SyncLock)
            {
                var command = new PauseCommand(MediaElement);
                Commands.Add(command);
                return command.Promise;
            }
        }

        public Task Stop()
        {
            lock (SyncLock)
            {
                Commands.Clear();
                var command = new StopCommand(MediaElement);
                Commands.Add(command);
                return command.Promise;
            }
        }

        public Task Seek(TimeSpan position)
        {
            lock (SyncLock)
            {
                // Remove prior queued, seek commands.
                if (Commands.Count > 0)
                {
                    var existingSeeks = Commands.FindAll(c => c.CommandType == MediaCommandType.Seek);
                    foreach (var seek in existingSeeks)
                        Commands.Remove(seek);
                }

                var command = new SeekCommand(MediaElement, position);
                Commands.Add(command);
                return command.Promise;
            }
        }

        public Task Close()
        {
            lock (SyncLock)
            {
                Commands.Clear();
                var command = new CloseCommand(MediaElement);
                var task = command.ExecuteAsync();
                return task;
            }
        }

        /// <summary>
        /// Processes the next command in the command queue.
        /// This method is called in every block rendering cycle.
        /// </summary>
        public async Task ProcessNext()
        {
            MediaCommand command = null;

            lock (SyncLock)
            {
                if (Commands.Count == 0) return;
                command = Commands[0];
                Commands.RemoveAt(0);
            }

            await command.ExecuteAsync();
        }

        #endregion

    }
}
