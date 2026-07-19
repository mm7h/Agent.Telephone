namespace Agent.Telephone.Common.Contexts
{
    internal class Workflow<T>
    {
        private string _deviceId = null!;
        //private long _turnId;
        private T _data = default!;

        public Workflow()
        {
        }

        public string DeviceId => this._deviceId;
        //public long TurnId => this._turnId;
        public T Data => this._data;


        public void Initialize(DeviceContext context, T data)
        {
            this._deviceId = context.DeviceId;
            this._data = data;
            //this._turnId = context.TurnId;
        }

        public void Reset()
        {
            this._deviceId = null!;
            this._data = default!;
            //this._turnId = 0;
        }
    }
}
