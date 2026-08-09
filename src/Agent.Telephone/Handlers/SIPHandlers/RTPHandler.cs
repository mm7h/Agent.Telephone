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
            this._rtpContext = this.ActiveCallContext.VoIPRTP;
            this._rtpContext.OnRtpPacketReceived += this.OnRtpPacketReceivedAsync;
            this._rtpContext.OnAudioFormatsNegotiated += this.OnAudioFormatsNegotiated;
            return true;
        }

        private async void OnRtpPacketReceivedAsync(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
        {
            if (mediaType != SDPMediaTypesEnum.audio || rtpPacket.Payload.Length == 0)
            {
                this.Logger.LogDebug("忽略非音频或空负载的 RTP 包。");
                return;
            }

            ActiveCallContext activeCall = this.ActiveCallContext;

            if (activeCall.IsAgentMediaPaused)
            {
                return;
            }

            if (rtpPacket.Header.PayloadType != activeCall.NegotiatedAudioFormat.FormatID)
            {
                this.Logger.LogDebug("忽略未协商的 RTP payload type {payloadType}。", rtpPacket.Header.PayloadType);
                return;
            }
            var workflow = this._rtpPacketWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, rtpPacket.Payload);
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
            this.ActiveCallContext.NegotiatedAudioFormat = audioFormats.FirstOrDefault(format => SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));
        }

        public override void Dispose()
        {
            if (this._rtpContext is not null)
            {
                this._rtpContext.OnRtpPacketReceived -= this.OnRtpPacketReceivedAsync;
            }
        }
    }
}
