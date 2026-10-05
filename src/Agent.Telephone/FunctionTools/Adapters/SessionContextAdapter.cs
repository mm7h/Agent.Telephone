using System.Net;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.FunctionTools.Adapters
{
    internal sealed class SessionContextAdapter : IDeviceContext
    {
        private readonly DeviceContext _deviceContext;

        public SessionContextAdapter(DeviceContext session)
        {
            this._deviceContext = session;
        }

        public string DeviceId => this._deviceContext.DeviceId;

        public DateTimeOffset LoginTime => this._deviceContext.LoginTime;

        public DateTimeOffset LastActiveTime => this._deviceContext.LastActivityTime;

        public IPEndPoint LocalEndPoint => this._deviceContext.LocalEndPoint.GetIPEndPoint();

        public IPEndPoint RemoteEndPoint => this._deviceContext.RemoteEndPoint.GetIPEndPoint();
    }
}
