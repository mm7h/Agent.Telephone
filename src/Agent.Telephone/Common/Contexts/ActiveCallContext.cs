using Agent.Telephone.Common.Constants;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class ActiveCallContext : IDisposable
    {
        private readonly CancellationTokenSource _callCts = new();
        private readonly object _turnLock = new();
        private CancellationTokenSource _turnCts;
        private long _turnId;

        public ActiveCallContext(SIPTransport sipTransport, SIPRequest sipRequest, DeviceContext deviceContext)
        {
            this._turnCts = CancellationTokenSource.CreateLinkedTokenSource(this._callCts.Token);
            this.UserAgent = new SIPUserAgent(sipTransport, SIPEndPoint.Empty, true);
            this.GetRemoteAudioPacketization(sipRequest);
            this.VoIPRTP = this.CreateVoIPMediaSession();
            this.AIAgentContext = new AIAgentContext(deviceContext);
        }

        public SIPUserAgent UserAgent { get; }
        public VoIPMediaSession VoIPRTP { get; }
        public int PacketTimeMs { get; private set; }
        public int MaxPacketTimeMs { get; private set; }
        public AudioFormat NegotiatedAudioFormat { get; set; } = AudioFormat.Empty;
        public AIAgentContext AIAgentContext { get; }
        public long TurnId => Interlocked.Read(ref this._turnId);
        public CancellationToken Token => this._turnCts.Token;
        public event Action<CancellationToken>? TurnTokenChanged;

        public void Cancel() => this._callCts.Cancel();

        public void RestartTurn()
        {
            CancellationTokenSource previous;
            CancellationToken nextToken;
            lock (this._turnLock)
            {
                if (this._callCts.IsCancellationRequested)
                {
                    return;
                }

                previous = this._turnCts;
                this._turnCts = CancellationTokenSource.CreateLinkedTokenSource(this._callCts.Token);
                Interlocked.Increment(ref this._turnId);
                nextToken = this._turnCts.Token;
            }

            previous.Cancel();
            previous.Dispose();
            this.TurnTokenChanged?.Invoke(nextToken);
        }

        private VoIPMediaSession CreateVoIPMediaSession()
        {
            AudioEncoder audioEncoder = new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat);
            AudioSourceOptions audioSourceOptions = new AudioSourceOptions { AudioSource = AudioSourcesEnum.None };
            AudioExtrasSource source = new AudioExtrasSource(audioEncoder, audioSourceOptions);
            source.RestrictFormats(format => SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));

            MediaEndPoints mediaEndPoints = new MediaEndPoints { AudioSource = source };
            VoIPMediaSession voipMediaSession = new VoIPMediaSession(mediaEndPoints) { AcceptRtpFromAny = true };
            return voipMediaSession;
        }

        private void GetRemoteAudioPacketization(SIPRequest request)
        {
            SDP? sdp = SDP.ParseSDPDescription(request.Body);
            SDPMediaAnnouncement? audio = sdp?.Media.FirstOrDefault(x => x.Media == SDPMediaTypesEnum.audio);

            // ptime/maxptime 正常应在音频媒体段；会话级回退仅作兼容。
            int? ptime = GetIntegerAttribute(audio?.ExtraMediaAttributes, "ptime")
                ?? GetIntegerAttribute(sdp?.ExtraSessionAttributes, "ptime");

            int? maxPtime = GetIntegerAttribute(audio?.ExtraMediaAttributes, "maxptime")
                ?? GetIntegerAttribute(sdp?.ExtraSessionAttributes, "maxptime");

            int packetTimeMs = ptime ?? AudioProcessSettings.DefaultPacketTimeMs;

            // maxptime 仅表示上限，不是建议采用的包时长。
            if (maxPtime is int max && packetTimeMs > max)
            {
                packetTimeMs = max;
            }
            this.PacketTimeMs = packetTimeMs;
            this.MaxPacketTimeMs = maxPtime ?? packetTimeMs;
        }
        private static int? GetIntegerAttribute(IEnumerable<string>? attributes, string name)
        {
            string prefix = $"a={name}:";

            string? attribute = attributes?.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            return attribute is not null
                && int.TryParse(attribute.AsSpan(prefix.Length), out int value)
                && value > 0
                    ? value
                    : null;
        }
        public void Dispose()
        {
            this._callCts.Cancel();
            this._turnCts.Dispose();
            this.VoIPRTP.Close("call ended");
            this._callCts.Dispose();
        }
    }
}
