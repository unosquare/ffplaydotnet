namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;
    using System;
    using Unosquare.FFplayDotNet.Core;

    /// <summary>
    /// Represents a media clock that is synchronizable
    /// to other clocks and FFmpeg packets
    /// Port of struct Clock
    /// </summary>
    public class Clock
    {
        #region Private Declarations

        /// <summary>
        /// The Clock speed ratio. Initially 1.0 set at the constructor
        /// Port of speed
        /// </summary>
        private double m_SpeedRatio = default(double);

        /// <summary>
        /// Pointer to the current packet queue serial, used for obsolete clock detection
        /// Port of *queue_serial
        /// </summary>
        private readonly Func<int?> GetPacketQueueSerial; // pointer to the current packet queue serial, used for obsolete clock detection

        /// <summary>
        /// Clock base minus time at which we updated the clock
        /// Port of pts_drift
        /// </summary>
        private double PtsDriftSeconds;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="Clock"/> class.
        /// Port of init_clock
        /// </summary>
        /// <param name="getPacketQueueSerialDelegate">The get packet queue serial delegate. This replaces the pointer in the original source code.</param>
        public Clock(Func<int?> getPacketQueueSerialDelegate)
        {
            GetPacketQueueSerial = getPacketQueueSerialDelegate; // ?? throw new ArgumentNullException(nameof(getPacketQueueSerialDelegate));
            SpeedRatio = 1.0;
            IsPaused = false;
            SetPosition(double.NaN, -1);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets a value indicating whether this clock is paused.
        /// Port of paused
        /// </summary>
        public bool IsPaused { get; internal set; }

        /// <summary>
        /// Gets the PTS. (clock base is the presentation timestamp)
        /// Port of pts
        /// </summary>
        public double PtsSeconds { get; private set; }

        /// <summary>
        /// Gets the last updated timestamp.
        /// Port of last_updated
        /// </summary>
        public double LastUpdatedSeconds { get; private set; }

        /// <summary>
        /// Gets the packet serial.
        /// clock is based on a packet with this serial
        /// Port of serial
        /// </summary>
        public int PacketSerial { get; private set; }

        /// <summary>
        /// Gets or sets the speed ratio.
        /// Port of speed
        /// </summary>
        public double SpeedRatio
        {
            get
            {
                return m_SpeedRatio;
            }
            set
            {
                SetPosition(PositionSeconds, PacketSerial);
                m_SpeedRatio = value;
            }
        }

        /// <summary>
        /// Gets the packet queue serial.
        /// Port of *queue_serial
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
        /// Port of get_clock
        /// </summary>
        public double PositionSeconds
        {
            get
            {
                if (PacketQueueSerial.HasValue == false || PacketQueueSerial.Value != PacketSerial)
                    return double.NaN;

                if (IsPaused)
                {
                    return PtsSeconds;
                }
                else
                {
                    var currentSeconds = ffmpeg.av_gettime_relative() / (double)ffmpeg.AV_TIME_BASE;
                    return PtsDriftSeconds + currentSeconds - (currentSeconds - LastUpdatedSeconds) * (1.0 - SpeedRatio);
                }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Sets the clock position with a specific Last Updated time.
        /// Port of set_clock
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="packetSerial">The serial.</param>
        /// <param name="timeInSeconds">The time.</param>
        public void SetPosition(double pts, int packetSerial, double timeInSeconds)
        {
            PtsSeconds = pts;
            LastUpdatedSeconds = timeInSeconds;
            PtsDriftSeconds = PtsSeconds - timeInSeconds;
            PacketSerial = packetSerial;
        }

        /// <summary>
        /// Sets the clock position with LastUpdated = the current timestamp.
        /// Port of set_clock
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="serial">The serial.</param>
        public void SetPosition(double pts, int serial)
        {
            var timeSeconds = ffmpeg.av_gettime_relative() / (double)ffmpeg.AV_TIME_BASE;
            SetPosition(pts, serial, timeSeconds);
        }

        /// <summary>
        /// Synchronizes this clock to a slave clock.
        /// Port of sync_clock_to_slave
        /// </summary>
        /// <param name="slave">The slave.</param>
        public void SyncTo(Clock slave)
        {
            var currentPosition = PositionSeconds;
            var slavePosition = slave.PositionSeconds;
            if (double.IsNaN(slavePosition)) return;

            if (double.IsNaN(currentPosition) 
                || Math.Abs(currentPosition - slavePosition) > Constants.AvNoSyncThresholdSecs)
            {
                SetPosition(slavePosition, slave.PacketSerial);
            }
                
        }

        #endregion
    }
}
