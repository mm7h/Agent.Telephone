using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using System.Net;

namespace Agent.Telephone.Handlers.SIPHandlers
{
    internal class RTPHandler : BaseHandler
    {
        private VoIPMediaSession? _rtpContext;

        public RTPHandler(IServiceProvider serviceProvider, ILogger<RTPHandler> logger) : base(serviceProvider, logger)
        {
        }
        public override string HandlerName => HandlerNames.RTPHandlerName;

        public override bool Build(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {

                return false;
            }
            this._rtpContext = deviceContext.ActiveCall.VoIPRTP;
            this._rtpContext.AudioExtrasSource.SetSource(AudioSourcesEnum.None);

            this._rtpContext.OnRtpPacketReceived += this.OnRtpPacketReceived;

            return true;
        }

        public void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
        {
            if (mediaType != SDPMediaTypesEnum.audio)
            {

                return;
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
