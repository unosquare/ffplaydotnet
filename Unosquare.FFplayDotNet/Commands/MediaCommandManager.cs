namespace Unosquare.FFplayDotNet.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Unosquare.FFplayDotNet.Commands;

    internal sealed class MediaCommandManager
    {
        private readonly object SyncLock = new object();
        private readonly List<MediaCommand> Commands = new List<MediaCommand>();
        private readonly MediaElement MediaElement;

        public MediaCommandManager(MediaElement mediaElement)
        {
            MediaElement = mediaElement;
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
                return command.ExecuteAsync();
            }
        }

        public Task Open(Uri uri)
        {
            lock (SyncLock)
            {
                Commands.Clear();
                var command = new OpenCommand(MediaElement, uri);
                return command.ExecuteAsync();
            }
        }

        private MediaCommand Dequeue()
        {
            lock (SyncLock)
            {
                if (Commands.Count == 0) return null;
                var command = Commands[0];
                Commands.RemoveAt(0);
                return command;
            }
        }

        public int Count { get { lock (SyncLock) return Commands.Count; } }

        /// <summary>
        /// Processes the next command in the command queue.
        /// This method is called in every rendering cycle.
        /// </summary>
        public async Task ProcessNext()
        {
            if (Count == 0) return;
            var command = Dequeue();
            await command.ExecuteAsync();
        }
    }
}
