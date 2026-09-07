namespace Agent.Telephone.Common.Contexts
{
    internal class Workflow<T>
    {
        private string _deviceId = null!;
        private string _callId = null!;
        private string? _callerNumber;
        private string? _dialedNumber;
        private long _turnId;
        private T _data = default!;

        public Workflow()
        {
        }

        public string DeviceId => this._deviceId;
        public string CallId => this._callId;
        public string? CallerNumber => this._callerNumber;
        public string? DialedNumber => this._dialedNumber;
        public long TurnId => this._turnId;
        public T Data => this._data;


        public void Initialize(ActiveCallContext activeCall, T data)
        {
            this.Initialize(activeCall, data, activeCall.TurnId);
        }

        public void Initialize(ActiveCallContext activeCall, T data, long turnId)
        {
            this._deviceId = activeCall.DeviceId;
            this._callId = activeCall.CallId;
            this._callerNumber = activeCall.CallerNumber;
            this._dialedNumber = activeCall.DialedNumber;
            this._data = data;
            this._turnId = turnId;
        }

        public void Reset()
        {
            this._deviceId = null!;
            this._callId = null!;
            this._callerNumber = null;
            this._dialedNumber = null;
            this._data = default!;
            this._turnId = 0;
        }
    }
}
