namespace Agent.Telephone.Common.Contexts
{
    internal class Workflow<T>
    {
        private string _deviceId = null!;
        private string _callId = null!;
        private long _turnId;
        private T _data = default!;

        public Workflow()
        {
        }

        public string DeviceId => this._deviceId;
        public string CallId => this._callId;
        public long TurnId => this._turnId;
        public T Data => this._data;


        public void Initialize(ActiveCallContext activeCall, T data)
        {
            this._deviceId = activeCall.DeviceId;
            this._callId = activeCall.CallId;
            this._data = data;
            this._turnId = activeCall.TurnId;
        }

        public void Reset()
        {
            this._deviceId = null!;
            this._callId = null!;
            this._data = default!;
            this._turnId = 0;
        }
    }
}
