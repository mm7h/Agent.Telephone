using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers.AIAdapterHandlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.SIPHandlers
{
    internal sealed class RTPHandler : BaseHandler, IOutAIAdapterHandler<byte[]>
    {
        private VoIPMediaSession? _rtpContext;

        private readonly ObjectPool<Workflow<byte[]>> _rtpPacketWorkflowPool;

        public RTPHandler(ObjectPool<Workflow<byte[]>> rtpPacketWorkflowPool,
            TelephoneConfig config, 
            ILogger<RTPHandler> logger) : base(config, logger)
        {
            this._rtpPacketWorkflowPool = rtpPacketWorkflowPool;
        }

        public override string HandlerName => HandlerNames.RTPHandlerName;

        public ChannelWriter<Workflow<byte[]>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法建立 RTP 上下文。", this.DeviceContext.DeviceId);
                return false;
            }
            this._rtpContext = this.DeviceContext.ActiveCall.VoIPRTP;
            this._rtpContext.OnRtpPacketReceived += this.OnRtpPacketReceived;
            this._rtpContext.OnAudioFormatsNegotiated += this.OnAudioFormatsNegotiated;
            return true;
        }

        private async void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
        {
            if (mediaType != SDPMediaTypesEnum.audio || rtpPacket.Payload.Length == 0)
            {
                this.Logger.LogDebug("忽略非音频或空负载的 RTP 包。");
                return;
            }

            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 收到 RTP 包，但未找到活动呼叫上下文。", this.DeviceContext.DeviceId);
                return;
            }

            if (rtpPacket.Header.PayloadType != this.DeviceContext.ActiveCall.NegotiatedAudioFormat.FormatID)
            {
                this.Logger.LogDebug("忽略未协商的 RTP payload type {payloadType}。", rtpPacket.Header.PayloadType);
                return;
            }
            var workflow = this._rtpPacketWorkflowPool.Get();
            workflow.Initialize(this.DeviceContext, rtpPacket.Payload);
            try
            {
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this._rtpPacketWorkflowPool.Return(workflow);
            }
        }

        private void OnAudioFormatsNegotiated(List<AudioFormat> audioFormats)
        {
            if (this.DeviceContext.ActiveCall is not null)
            {
                this.DeviceContext.ActiveCall.NegotiatedAudioFormat = audioFormats.FirstOrDefault(format => format.Codec == AudioCodecsEnum.PCMU || format.Codec == AudioCodecsEnum.PCMA);
            }
            else
            { 
                this.Logger.LogWarning("设备 {deviceId} 的音频格式协商完成，但未找到活动呼叫上下文。", this.DeviceContext.DeviceId);
            }
        }

        public override void Dispose()
        {
            if (this._rtpContext is not null)
            {
                this._rtpContext.OnRtpPacketReceived -= this.OnRtpPacketReceived;
            }
        }
    }
}
