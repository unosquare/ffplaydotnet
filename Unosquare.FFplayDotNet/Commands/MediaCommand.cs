namespace Unosquare.FFplayDotNet.Commands
{
    using System.Threading.Tasks;

    /// <summary>
    /// Represents a command to be executed against an intance of the MediaElement
    /// </summary>
    internal abstract class MediaCommand
    {
        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaCommand"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        /// <param name="commandType">Type of the command.</param>
        protected MediaCommand(MediaElement mediaElement, MediaCommandType commandType)
        {
            MediaElement = mediaElement;
            CommandType = commandType;
            Promise = new Task(Execute);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the associated media element.
        /// </summary>
        public MediaElement MediaElement { get; private set; }

        /// <summary>
        /// Gets the type of the command.
        /// </summary>
        public MediaCommandType CommandType { get; private set; }

        /// <summary>
        /// Gets the promise-mode Task. You can wait for this task
        /// </summary>
        public Task Promise { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Executes this command asynchronously
        /// by starting the associated promise and awaiting it.
        /// </summary>
        /// <returns></returns>
        public async Task ExecuteAsync()
        {
            Promise.Start();
            await Promise;
        }

        /// <summary>
        /// Performs the actions that this command implements.
        /// </summary>
        protected abstract void Execute();

        #endregion
    }
}
