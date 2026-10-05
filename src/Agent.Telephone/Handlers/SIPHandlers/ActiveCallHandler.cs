using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Handlers.SIPHandlers
{
    internal sealed class ActiveCallHandler : BaseHandler
    {
        public ActiveCallHandler(TelephoneConfig config, ILogger<ActiveCallHandler> logger) : base(config, logger) { }

        public override string HandlerName => HandlerNames.ActiveCallHandlerName;

        public override bool Build()
        {
            ActiveCallContext activeCall = this.ActiveCallContext;
            activeCall.UserAgent.OnCallHungup += this.OnCallHungup;
            activeCall.UserAgent.ServerCallCancelled += this.OnServerCallCancelled;
            activeCall.UserAgent.OnDtmfTone += this.OnDtmfTone;
            return true;
        }

        public async Task<bool> AnswerAsync(SIPRequest request)
        {
            IDisposable? lease = null;
            ActiveCallContext activeCall = this.ActiveCallContext;

            if (activeCall.TryAcquireUse(out lease) && lease is not null)
            {
                using (lease)
                {
                    var serverAgent = this.ActiveCallContext.DeviceContext.TakePendingServerUserAgent(activeCall);
                    if (serverAgent is null)
                    {
                        this.Logger.LogWarning("设备 {deviceId} 的呼叫已取消或没有待应答事务。", this.ActiveCallContext.DeviceId);
                        return false;
                    }

                    bool answered = await activeCall.UserAgent.Answer(serverAgent, activeCall.VoIPRTP);
                    if (answered && !activeCall.NegotiatedAudioFormat.IsEmpty())
                    {
                        this.ActiveCallContext.DeviceContext.MarkCallConnected(activeCall);
                        return true;
                    }
                    else
                    {
                        this.Logger.LogWarning("设备 {deviceId} 未能协商 PCMU/PCMA 音频。", this.ActiveCallContext.DeviceId);
                        return false;
                    }
                }
            }
            else
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法应答。", this.ActiveCallContext.DeviceId);
                return false;
            }
        }

        private void OnCallHungup(SIPDialogue dialogue)
        {
            this.CloseActiveCallSession();
        }
        private void OnServerCallCancelled(SIPSorcery.SIP.App.ISIPServerUserAgent agent, SIPRequest request)
        {
            this.CloseActiveCallSession();
        }

        private void OnDtmfTone(byte tone, int duration)
        {
            this.ActiveCallContext.AIAgentContext.PrivateProvider.DtmfInput?.HandleDtmfTone(this.ActiveCallContext, tone);
        }

        private void CloseActiveCallSession()
        {
            this.ActiveCallContext.DeviceContext.CloseCallSession(this.ActiveCallContext);
        }

        protected override void DisposeResources()
        {
            ActiveCallContext activeCall = this.ActiveCallContext;
            if (activeCall is not null)
            {
                activeCall.UserAgent.OnCallHungup -= this.OnCallHungup;
                activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
                activeCall.UserAgent.OnDtmfTone -= this.OnDtmfTone;
            }
        }
    }
}
