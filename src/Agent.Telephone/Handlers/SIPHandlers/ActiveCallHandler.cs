using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Telephone.Handlers.SIPHandlers
{
    internal class ActiveCallHandler : BaseHandler
    {
        private SIPUserAgent? _sipUserAgent;

        public ActiveCallHandler(IServiceProvider serviceProvider, ILogger<ActiveCallHandler> logger) : base(serviceProvider, logger)
        {
        }
        public override string HandlerName => HandlerNames.ActiveCallHandlerName;

        public override bool Build(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {

                return false;
            }
            this._sipUserAgent = deviceContext.ActiveCall.UserAgent;
            this._sipUserAgent.OnIncomingCall += this.OnIncomingCall;
            this._sipUserAgent.ClientCallRinging += this.OnClientCallRinging;
            this._sipUserAgent.ServerCallCancelled += this.OnServerCallCancelled;
            this._sipUserAgent.OnDtmfTone += this.OnDtmfTone;
            this._sipUserAgent.OnCallHungup += this.OnCallHungup;

            return true;
        }

        private void OnIncomingCall(SIPUserAgent sipUserAgent, SIPRequest sipRequest)
        {

        }

        private void OnClientCallRinging(ISIPClientUserAgent uac, SIPResponse sipResponse)
        {

        }

        private void OnServerCallCancelled(ISIPServerUserAgent uas, SIPRequest cancelRequest)
        {

        }

        private void OnDtmfTone(byte tone, int duration)
        {

        }

        private void OnCallHungup(SIPDialogue sipDialogue)
        {

        }

        public override void Dispose()
        {
            if (_sipUserAgent is not null)
            {
                this._sipUserAgent.OnIncomingCall -= this.OnIncomingCall;
                this._sipUserAgent.ClientCallRinging -= this.OnClientCallRinging;
                this._sipUserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
                this._sipUserAgent.OnDtmfTone -= this.OnDtmfTone;
                this._sipUserAgent.OnCallHungup -= this.OnCallHungup;
            }
        }
    }
}
