using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
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
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogError("设备 {deviceId} 没有活动呼叫上下文。", this.DeviceContext.DeviceId);
                return false;
            }
            this.DeviceContext.ActiveCall.UserAgent.OnCallHungup += this.OnCallHungup;
            this.DeviceContext.ActiveCall.UserAgent.ServerCallCancelled += this.OnServerCallCancelled;
            return true;
        }

        public async Task<bool> AnswerAsync(SIPRequest request)
        {
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法应答。", this.DeviceContext.DeviceId);
                return false;
            }
            var serverAgent = this.DeviceContext.ActiveCall.UserAgent.AcceptCall(request);
            bool answered = await this.DeviceContext.ActiveCall.UserAgent.Answer(serverAgent, this.DeviceContext.ActiveCall.VoIPRTP);
            if (!answered || this.DeviceContext.ActiveCall.NegotiatedAudioFormat.IsEmpty())
            {
                this.Logger.LogWarning("设备 {deviceId} 未能协商 PCMU/PCMA 音频。", this.DeviceContext.DeviceId);
                return false;
            }
            return true;
        }

        private void OnCallHungup(SIPDialogue dialogue)
        {
            this.DeviceContext.CloseCallSession();
        }
        private void OnServerCallCancelled(SIPSorcery.SIP.App.ISIPServerUserAgent agent, SIPRequest request)
        {
            this.DeviceContext.CloseCallSession();
        }

        public override void Dispose()
        {
            if (this.DeviceContext.ActiveCall is not null)
            {
                this.DeviceContext.ActiveCall.UserAgent.OnCallHungup -= this.OnCallHungup;
                this.DeviceContext.ActiveCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
            }
        }
    }
}
