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
        private readonly string _userAor;
        private readonly CancellationTokenSource _callCts = new();
        private readonly object _turnLock = new();
        private readonly object _lifetimeLock = new();
        private readonly List<IDisposable> _ownedResources = [];
        private CancellationTokenSource _turnCts;
        private long _turnId;
        private int _useCount;
        private bool _disposeRequested;
        private bool _callEnded;
        private int _agentMediaPaused;

        public ActiveCallContext(SIPTransport sipTransport, SIPRequest sipRequest, DeviceContext deviceContext)
        {
            this._deviceContext = deviceContext;
            this._userAor = deviceContext.Registration?.Aor.ToString()
                ?? throw new InvalidOperationException("The device has no active registration.");
            this.CallId = Guid.NewGuid().ToString("N");
            this.DeviceId = deviceContext.DeviceId;
            this._turnCts = new CancellationTokenSource();

            this.CreateSIPUserAgent(sipTransport);
            this.CreateVoIPMediaSession();
            this.GetRemoteAudioPacketization(sipRequest);
            this.GetPhoneNumbers(sipRequest);
            this.CreateAIAgentContext();
        }

        public ActiveCallContext(
            DeviceContext deviceContext,
            string userAor,
            string assistantNumber,
            SIPUserAgent userAgent,
            VoIPMediaSession mediaSession)
        {
            this._deviceContext = deviceContext;
            this._userAor = userAor;
            this.CallId = Guid.NewGuid().ToString("N");
            this.DeviceId = deviceContext.DeviceId;
            this._turnCts = new CancellationTokenSource();
            this.UserAgent = userAgent;
            this.VoIPRTP = mediaSession;
            this.PacketTimeMs = AudioProcessSettings.DefaultPacketTimeMs;
            this.MaxPacketTimeMs = this.PacketTimeMs;
            this.CallerNumber = SIPURI.ParseSIPURI(userAor).User;
            this.DialedNumber = assistantNumber;
            this.CreateAIAgentContext();
        }
        public string CallId { get; }
        public string DeviceId { get; }
        public string UserAor => this._userAor;
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
        public CancellationToken CallToken => this._callCts.Token;
        public CancellationToken Token => this._turnCts.Token;
        public bool IsAgentMediaPaused => Volatile.Read(ref this._agentMediaPaused) != 0;
        public event Action<CancellationToken>? TurnTokenChanged;

        public void Cancel() => this._callCts.Cancel();

        public void PauseAgentMedia() => Interlocked.Exchange(ref this._agentMediaPaused, 1);
        public void ResumeAgentMedia() => Interlocked.Exchange(ref this._agentMediaPaused, 0);
        public void MarkTransferDialing() =>
            this._deviceContext.MarkTransferDialing(this);
        public void MarkBridged() =>
            this._deviceContext.MarkBridged(this);
        public void MarkPlayingPrompt() =>
            this._deviceContext.MarkPlayingPrompt(this);
        public void MarkEnding() =>
            this._deviceContext.MarkCallEnding(this);

        public bool TryAcquireUse(out IDisposable? lease)
        {
            lock (this._lifetimeLock)
            {
                if (this._disposeRequested)
                {
                    lease = null;
                    return false;
                }

                this._useCount++;
                lease = new CallUseLease(this);
                return true;
            }
        }

        public void RegisterOwnedResource(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);

            lock (this._lifetimeLock)
            {
                if (this._disposeRequested)
                {
                    throw new ObjectDisposedException(nameof(ActiveCallContext));
                }

                this._ownedResources.Add(resource);
            }
        }

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
                this._turnCts = new CancellationTokenSource();
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
            this.EndCallTransport();

            lock (this._lifetimeLock)
            {
                if (this._disposeRequested)
                {
                    return;
                }

                this._disposeRequested = true;
                if (this._useCount > 0)
                {
                    return;
                }
            }

            this.DisposeCore();
        }

        private void ReleaseUse()
        {
            bool shouldDispose = false;
            lock (this._lifetimeLock)
            {
                this._useCount--;
                shouldDispose = this._disposeRequested && this._useCount == 0;
            }

            if (shouldDispose)
            {
                ThreadPool.QueueUserWorkItem(
                    static state => ((ActiveCallContext)state!).DisposeCore(),
                    this);
            }
        }

        private void DisposeCore()
        {
            this.EndCallTransport();
            this._turnCts.Cancel();
            for (int index = this._ownedResources.Count - 1; index >= 0; index--)
            {
                this._ownedResources[index].Dispose();
            }
            this._ownedResources.Clear();
            this.AIAgentContext.Dispose();
            this._turnCts.Dispose();
            this._callCts.Dispose();
        }

        private void EndCallTransport()
        {
            lock (this._lifetimeLock)
            {
                if (this._callEnded)
                {
                    return;
                }

                this._callEnded = true;
            }

            this._callCts.Cancel();
            this.VoIPRTP.Close("call ended");
        }

        private sealed class CallUseLease : IDisposable
        {
            private ActiveCallContext? _activeCall;

            public CallUseLease(ActiveCallContext activeCall)
            {
                this._activeCall = activeCall;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref this._activeCall, null)?.ReleaseUse();
            }
        }
    }
}
