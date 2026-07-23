using System.Diagnostics.CodeAnalysis;
using Agent.Telephone.Abstractions.Configs;
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
        private readonly DeviceContext _deviceContext;
        private readonly CancellationTokenSource _callCts = new();
        private readonly object _turnLock = new();
        private CancellationTokenSource _turnCts;
        private long _turnId;

        public ActiveCallContext(SIPTransport sipTransport, SIPRequest sipRequest, DeviceContext deviceContext)
        {
            this._deviceContext = deviceContext;
            this.DeviceId = deviceContext.DeviceId;
            this._turnCts = CancellationTokenSource.CreateLinkedTokenSource(this._callCts.Token);

            this.CreateSIPUserAgent(sipTransport);
            this.CreateVoIPMediaSession();
            this.GetRemoteAudioPacketization(sipRequest);
            this.GetPhoneNumbers(sipRequest);
            this.CreateAIAgentContext();
        }
        public string DeviceId { get; }
        public SIPUserAgent UserAgent { get; private set; }
        public VoIPMediaSession VoIPRTP { get; private set; }
        public int PacketTimeMs { get; private set; }
        public int MaxPacketTimeMs { get; private set; }
        public string? CallerNumber { get; private set; }
        public string? DialedNumber { get; private set; }
        public AudioFormat NegotiatedAudioFormat { get; set; } = AudioFormat.Empty;
        public AIAgentContext AIAgentContext { get; private set; }
        public AssistantConfig AssistantConfig { get; private set; }
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

        [MemberNotNull(nameof(this.UserAgent))]
        private void CreateSIPUserAgent(SIPTransport sipTransport)
        {
            this.UserAgent = new SIPUserAgent(sipTransport, SIPEndPoint.Empty, true);
        }

        [MemberNotNull(nameof(this.VoIPRTP))]
        private void CreateVoIPMediaSession()
        {
            AudioEncoder audioEncoder = new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat);
            AudioSourceOptions audioSourceOptions = new AudioSourceOptions { AudioSource = AudioSourcesEnum.None };
            AudioExtrasSource source = new AudioExtrasSource(audioEncoder, audioSourceOptions);
            source.RestrictFormats(format => SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));

            MediaEndPoints mediaEndPoints = new MediaEndPoints { AudioSource = source };
            VoIPMediaSession voipMediaSession = new VoIPMediaSession(mediaEndPoints) { AcceptRtpFromAny = true };

            this.VoIPRTP = voipMediaSession;
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

        private void GetPhoneNumbers(SIPRequest sipRequest)
        {
            // 主叫号码（来电来源）
            this.CallerNumber = sipRequest.Header.From?.FromURI?.User;

            // 被叫号码（座机拨打的本服务号码）
            this.DialedNumber = sipRequest.URI?.User
                ?? sipRequest.Header.To?.ToURI?.User;
        }

        [MemberNotNull(nameof(this.AIAgentContext), nameof(this.AssistantConfig))]
        private void CreateAIAgentContext()
        {
            if (string.IsNullOrWhiteSpace(this.DialedNumber))
            {
                throw new InvalidOperationException("呼叫号码不能为空");
            }
            if (this._deviceContext.AvailableAssistants.TryGetValue(this.DialedNumber, out AssistantConfig? assistantConfig) && assistantConfig is not null)
            {
                this.AssistantConfig = assistantConfig;
                this.AIAgentContext = new AIAgentContext(this);
            }
            else
            {
                throw new InvalidOperationException($"无法获取AI助手信息，呼叫的号码：{this.DialedNumber}");
            }
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
