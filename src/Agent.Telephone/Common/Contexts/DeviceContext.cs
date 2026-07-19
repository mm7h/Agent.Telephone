using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Telephone.Common.Contexts
{
    internal class DeviceContext : IDisposable
    {
        private readonly SIPTransport _sipTransport;

        public DeviceContext(string deviceId, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            this.DeviceId = deviceId;
            this.Contact = sipRequest.URI;
            this.RemoteEndPoint = sipRequest.RemoteSIPEndPoint;
            this.AIAgent = new AIAgentContext();

            this._sipTransport = sipTransport;
        }

        public string DeviceId { get; private set; }
        public SIPURI Contact { get; private set; }

        public SIPEndPoint RemoteEndPoint { get; private set; }
        public ActiveCallContext? ActiveCall { get; private set; }
        public AIAgentContext AIAgent { get; private set; }
        public void InitializeCallSession(SIPRequest sipRequest)
        {

            this.ActiveCall = new ActiveCallContext(this._sipTransport);

        }

        public void Dispose()
        {

        }

    }
}
