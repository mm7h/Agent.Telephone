namespace Agent.Telephone.Common.Contexts
{
    internal class Workflow<T>
    {
        private string _deviceId = null!;
        private string _callId = null!;
        private long _turnId;
        private bool _isFinal;
        private T _data = default!;

        public Workflow()
        {
        }

        public string DeviceId => this._deviceId;
        public string CallId => this._callId;
        public long TurnId => this._turnId;
        public bool IsFinal => this._isFinal;
        public T Data => this._data;


        public void Initialize(DeviceContext context, T data, bool isFinal = false)
        {
            ActiveCallContext activeCall = context.ActiveCall
                ?? throw new InvalidOperationException("An active call is required to initialize a workflow.");
            this.Initialize(activeCall, data, isFinal);
        }

        public void Initialize(ActiveCallContext activeCall, T data, bool isFinal = false)
        {
            this._deviceId = activeCall.DeviceId;
            this._callId = activeCall.CallId;
            this._data = data;
            this._turnId = activeCall.TurnId;
            this._isFinal = isFinal;
        }

        public void Reset()
        {
            this._deviceId = null!;
            this._callId = null!;
            this._data = default!;
            this._turnId = 0;
            this._isFinal = false;
        }
    }
}
