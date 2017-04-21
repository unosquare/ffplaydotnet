namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;

    /// <summary>
    /// Represents a media clock that is synchronizable
    /// to other clocks and FFmpeg packets
    /// </summary>
    public class Clock
    {
        #region Private Declarations

        /// <summary>
        /// The Clock speed ratio. Initially 1.0
        /// </summary>
        private double m_SpeedRatio = default(double);

        /// <summary>
        /// Pointer to the current packet queue serial, used for obsolete clock detection
        /// </summary>
        private readonly Func<int?> GetPacketQueueSerial; // pointer to the current packet queue serial, used for obsolete clock detection

        /// <summary>
        /// Clock base minus time at which we updated the clock
        /// </summary>
        private double PtsDrift;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="Clock"/> class.
        /// </summary>
        /// <param name="getPacketQueueSerialDelegate">The get packet queue serial delegate. This replaces the pointer in the original source code.</param>
        public Clock(Func<int?> getPacketQueueSerialDelegate)
        {
            if (getPacketQueueSerialDelegate == null)
                throw new ArgumentNullException(nameof(getPacketQueueSerialDelegate));

            SpeedRatio = 1.0;
            IsPaused = false;
            GetPacketQueueSerial = getPacketQueueSerialDelegate;
            SetPosition(double.NaN, -1);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets a value indicating whether this clock is paused.
        /// </summary>
        public bool IsPaused { get; internal set; }

        /// <summary>
        /// Gets the PTS. (clock base is the presentation timestamp)
        /// </summary>
        public double Pts { get; private set; }

        public double LastUpdated { get; private set; }

        /// <summary>
        /// Gets the packet serial.
        /// clock is based on a packet with this serial
        /// </summary>
        public int PacketSerial { get; private set; }

        /// <summary>
        /// Gets or sets the speed ratio.
        /// </summary>
        public double SpeedRatio
        {
            get
            {
                return m_SpeedRatio;
            }
            set
            {
                SetPosition(Position, PacketSerial);
                m_SpeedRatio = value;
            }
        }

        /// <summary>
        /// Gets the packet queue serial.
        /// </summary>
        public int? PacketQueueSerial
        {
            get
            {
                if (GetPacketQueueSerial == null) return PacketSerial;
                return GetPacketQueueSerial();
            }
        }

        /// <summary>
        /// Gets the position.
        /// </summary>
        public double Position
        {
            get
            {
                if (GetPacketQueueSerial().HasValue == false || GetPacketQueueSerial().Value != PacketSerial)
                    return double.NaN;

                if (IsPaused)
                {
                    return Pts;
                }
                else
                {
                    var time = ffmpeg.av_gettime_relative() / 1000000.0;
                    return PtsDrift + time - (time - LastUpdated) * (1.0 - SpeedRatio);
                }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Sets the clock position with a specific Last Updated time.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="serial">The serial.</param>
        /// <param name="time">The time.</param>
        public void SetPosition(double pts, int serial, double time)
        {
            Pts = pts;
            LastUpdated = time;
            PtsDrift = Pts - time;
            PacketSerial = serial;
        }

        /// <summary>
        /// Sets the clock position with LastUpdated = the current timestamp.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="serial">The serial.</param>
        public void SetPosition(double pts, int serial)
        {
            var time = ffmpeg.av_gettime_relative() / 1000000.0;
            SetPosition(pts, serial, time);
        }

        /// <summary>
        /// Synchronizes this clock to a slave clock.
        /// </summary>
        /// <param name="slave">The slave.</param>
        public void SyncTo(Clock slave)
        {
            var currentPosition = Position;
            var slavePosition = slave.Position;
            if (double.IsNaN(slavePosition)) return;

            if (double.IsNaN(currentPosition) 
                || Math.Abs(currentPosition - slavePosition) > Constants.AvNoSyncThreshold)
            {
                SetPosition(slavePosition, slave.PacketSerial);
            }
                
        }

        #endregion
    }
}
