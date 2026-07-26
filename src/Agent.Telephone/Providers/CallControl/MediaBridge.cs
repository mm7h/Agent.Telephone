using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace Agent.Telephone.Providers.CallControl
{
    /// <summary>
    /// Relays encoded PCMU/PCMA payloads between two independently negotiated RTP
    /// sessions. SendAudio deliberately creates a fresh RTP header for each leg.
    /// </summary>
    internal sealed class MediaBridge : IDisposable
    {
        private readonly VoIPMediaSession _first;
        private readonly VoIPMediaSession _second;
        private readonly int _firstAudioPayloadType;
        private readonly int _secondAudioPayloadType;
        private readonly CancellationTokenSource _cts = new();
        private readonly ILogger _logger;
        private readonly RtpRelayState _firstRelayState;
        private readonly RtpRelayState _secondRelayState;
        private int _disposed;

        public MediaBridge(
            VoIPMediaSession first,
            int firstAudioPayloadType,
            VoIPMediaSession second,
            int secondAudioPayloadType,
            uint defaultDurationRtpUnits,
            ILogger logger)
        {
            this._first = first;
            this._firstAudioPayloadType = firstAudioPayloadType;
            this._second = second;
            this._secondAudioPayloadType = secondAudioPayloadType;
            this._firstRelayState = new RtpRelayState(defaultDurationRtpUnits);
            this._secondRelayState = new RtpRelayState(defaultDurationRtpUnits);
            this._logger = logger;

            this._first.OnRtpPacketReceived += this.OnFirstRtpPacketReceived;
            this._second.OnRtpPacketReceived += this.OnSecondRtpPacketReceived;
            this._first.OnRtpEvent += this.OnFirstRtpEvent;
            this._second.OnRtpEvent += this.OnSecondRtpEvent;
        }

        private void OnFirstRtpPacketReceived(
            IPEndPoint remoteEndPoint,
            SDPMediaTypesEnum mediaType,
            RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio &&
                packet.Payload.Length > 0 &&
                packet.Header.PayloadType == this._firstAudioPayloadType)
            {
                this._second.SendAudio(
                    this._firstRelayState.GetDuration(packet.Header.Timestamp),
                    packet.Payload);
            }
        }

        private void OnSecondRtpPacketReceived(
            IPEndPoint remoteEndPoint,
            SDPMediaTypesEnum mediaType,
            RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio &&
                packet.Payload.Length > 0 &&
                packet.Header.PayloadType == this._secondAudioPayloadType)
            {
                this._first.SendAudio(
                    this._secondRelayState.GetDuration(packet.Header.Timestamp),
                    packet.Payload);
            }
        }

        private void OnFirstRtpEvent(IPEndPoint remoteEndPoint, RTPEvent rtpEvent, RTPHeader rtpHeader)
        {
            if (this._firstRelayState.ShouldForwardDtmf(
                rtpEvent.EventID,
                rtpHeader.Timestamp,
                rtpEvent.EndOfEvent))
            {
                this.ForwardDtmfAsync(this._second, rtpEvent.EventID);
            }
        }

        private void OnSecondRtpEvent(IPEndPoint remoteEndPoint, RTPEvent rtpEvent, RTPHeader rtpHeader)
        {
            if (this._secondRelayState.ShouldForwardDtmf(
                rtpEvent.EventID,
                rtpHeader.Timestamp,
                rtpEvent.EndOfEvent))
            {
                this.ForwardDtmfAsync(this._first, rtpEvent.EventID);
            }
        }

        private async void ForwardDtmfAsync(VoIPMediaSession destination, byte eventId)
        {
            try
            {
                await destination.SendDtmf(eventId, this._cts.Token);
            }
            catch (OperationCanceledException) when (this._cts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(exception, "转发 RFC2833 DTMF 事件 {EventId} 失败。", eventId);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) != 0)
            {
                return;
            }

            this._cts.Cancel();
            this._first.OnRtpPacketReceived -= this.OnFirstRtpPacketReceived;
            this._second.OnRtpPacketReceived -= this.OnSecondRtpPacketReceived;
            this._first.OnRtpEvent -= this.OnFirstRtpEvent;
            this._second.OnRtpEvent -= this.OnSecondRtpEvent;
            this._cts.Dispose();
        }
    }
}
