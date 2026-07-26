namespace Agent.Telephone.Providers.CallControl
{
    internal sealed class RtpRelayState
    {
        private readonly object _sync = new();
        private readonly uint _defaultDurationRtpUnits;
        private uint? _previousTimestamp;
        private (byte EventId, uint Timestamp)? _lastDtmf;

        public RtpRelayState(uint defaultDurationRtpUnits)
        {
            this._defaultDurationRtpUnits = Math.Max(1, defaultDurationRtpUnits);
        }

        public uint GetDuration(uint timestamp)
        {
            lock (this._sync)
            {
                uint duration = this._previousTimestamp.HasValue
                    ? timestamp - this._previousTimestamp.Value
                    : this._defaultDurationRtpUnits;
                this._previousTimestamp = timestamp;
                return duration is > 0 and <= 8000
                    ? duration
                    : this._defaultDurationRtpUnits;
            }
        }

        public bool ShouldForwardDtmf(byte eventId, uint timestamp, bool endOfEvent)
        {
            if (!endOfEvent)
            {
                return false;
            }

            lock (this._sync)
            {
                (byte EventId, uint Timestamp) current = (eventId, timestamp);
                if (this._lastDtmf == current)
                {
                    return false;
                }

                this._lastDtmf = current;
                return true;
            }
        }
    }
}
