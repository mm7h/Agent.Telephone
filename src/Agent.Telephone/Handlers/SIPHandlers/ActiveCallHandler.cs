using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Handlers.SIPHandlers
{
    internal sealed class ActiveCallHandler : BaseHandler
    {
        private ActiveCallContext? _activeCall;

        public ActiveCallHandler(TelephoneConfig config, ILogger<ActiveCallHandler> logger) : base(config, logger) { }

        public override string HandlerName => HandlerNames.ActiveCallHandlerName;

        public override bool Build()
        {
            ActiveCallContext? activeCall = this.DeviceContext.ActiveCall;
            if (activeCall is null)
            {
                this.Logger.LogError("设备 {deviceId} 没有活动呼叫上下文。", this.DeviceContext.DeviceId);
                return false;
            }
            this._activeCall = activeCall;
            activeCall.UserAgent.OnCallHungup += this.OnCallHungup;
            activeCall.UserAgent.ServerCallCancelled += this.OnServerCallCancelled;
            activeCall.UserAgent.OnDtmfTone += this.OnDtmfTone;
            return true;
        }

        public async Task<bool> AnswerAsync(SIPRequest request)
        {
            ActiveCallContext? activeCall = this._activeCall;
            IDisposable? lease = null;
            if (activeCall is not null && activeCall.TryAcquireUse(out lease) && lease is not null)
            {
                using (lease)
                {
                    var serverAgent = this.DeviceContext.TakePendingServerUserAgent(activeCall);
                    if (serverAgent is null)
                    {
                        this.Logger.LogWarning("设备 {deviceId} 的呼叫已取消或没有待应答事务。", this.DeviceContext.DeviceId);
                        return false;
                    }

                    bool answered = await activeCall.UserAgent.Answer(serverAgent, activeCall.VoIPRTP);
                    if (answered && !activeCall.NegotiatedAudioFormat.IsEmpty())
                    {
                        this.DeviceContext.MarkCallConnected(activeCall);
                        return true;
                    }
                    else
                    {
                        this.Logger.LogWarning("设备 {deviceId} 未能协商 PCMU/PCMA 音频。", this.DeviceContext.DeviceId);
                        return false;
                    }
                }
            }
            else
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法应答。", this.DeviceContext.DeviceId);
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
            ActiveCallContext? activeCall = this._activeCall;
            activeCall?.AIAgentContext.PrivateProvider.DtmfInput?.HandleDtmfTone(activeCall, tone);
        }

        private void CloseActiveCallSession()
        {
            if (this._activeCall is not null)
            {
                this.DeviceContext.CloseCallSession(this._activeCall);
            }
        }

        public override void Dispose()
        {
            if (this._activeCall is not null)
            {
                this._activeCall.UserAgent.OnCallHungup -= this.OnCallHungup;
                this._activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
                this._activeCall.UserAgent.OnDtmfTone -= this.OnDtmfTone;
            }
        }
    }
}
