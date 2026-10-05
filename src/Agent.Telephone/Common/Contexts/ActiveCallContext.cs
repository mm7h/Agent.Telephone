using System.Diagnostics.CodeAnalysis;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Microsoft.Extensions.DependencyInjection;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class ActiveCallContext : IDisposable
    {
        // G.711 电话 RTP 保持小帧，以满足实时性并避免超出底层 UDP 接收缓冲区。
        private const int MinimumPacketTimeMs = 10;
        private const int MaximumPacketTimeMs = 60;
        private readonly AsyncServiceScope _serviceScope;
        private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposeStarted;
        private readonly string _userAor;
        private readonly CancellationTokenSource _callCts = new();
        private readonly CancellationToken _callToken;
        private readonly object _turnLock = new();
        private readonly object _lifetimeLock = new();
        private readonly object _agentSessionLock = new();
        private readonly object _promptPlaybackLock = new();
        private readonly List<IDisposable> _callOwnedResources = [];
        private CancellationTokenSource _turnCts;
        private CancellationToken _turnToken;
        private bool _stopping;
        private long _turnId;
        private int _useCount;
        private volatile bool _disposeRequested;
        private bool _transportEnded;
        private bool _callEnded;
        private bool _assistantSwitching;
        private int _agentMediaPaused;
        private int _userAudioInputPaused;
        private long _userAudioProcessingTurnId = -1;
        private long _hangupAfterReplyTurnId = -1;
        private TaskCompletionSource<bool>? _promptPlaybackCompletion;

        public ActiveCallContext(SIPTransport sipTransport, SIPRequest sipRequest, DeviceContext deviceContext)
        {
            this.DeviceContext = deviceContext;
            this._userAor = deviceContext.Registration?.Aor.ToString()
                ?? throw new InvalidOperationException("The device has no active registration.");
            this.CallId = Guid.NewGuid().ToString("N");
            this.DeviceId = deviceContext.DeviceId;
            this._turnCts = new CancellationTokenSource();
            this._turnToken = this._turnCts.Token;
            this._callToken = this._callCts.Token;

            this._serviceScope = deviceContext.ServiceScopeFactory.CreateAsyncScope();
            try
            {
                this.CreateSIPUserAgent(sipTransport);
                this.CreateVoIPMediaSession();
                this.GetRemoteAudioPacketization(sipRequest);
                this.GetPhoneNumbers(sipRequest);
                this.CreateAIAgentContext();
                deviceContext.TrackCall(this);
            }
            catch
            {
                this.AIAgentContext?.Dispose();
                this._serviceScope.Dispose();
                this._turnCts.Dispose();
                this._callCts.Dispose();
                this.VoIPRTP?.Close("call initialization failed");
                throw;
            }
        }

        public ActiveCallContext(
            DeviceContext deviceContext,
            string userAor,
            string assistantNumber,
            SIPUserAgent userAgent,
            VoIPMediaSession mediaSession)
        {
            this.DeviceContext = deviceContext;
            this._userAor = userAor;
            this.CallId = Guid.NewGuid().ToString("N");
            this.DeviceId = deviceContext.DeviceId;
            this._turnCts = new CancellationTokenSource();
            this._turnToken = this._turnCts.Token;
            this._callToken = this._callCts.Token;
            this.UserAgent = userAgent;
            this.VoIPRTP = mediaSession;
            this._serviceScope = deviceContext.ServiceScopeFactory.CreateAsyncScope();
            try
            {
                this.NegotiatedAudioFormat = mediaSession.AudioStream.GetSendingFormat().ToAudioFormat();
                this.PacketTimeMs = AudioProcessSettings.DefaultPacketTimeMs;
                this.MaxPacketTimeMs = this.PacketTimeMs;
                this.CallerNumber = SIPURI.ParseSIPURI(userAor).User;
                this.DialedNumber = assistantNumber;
                this.CreateAIAgentContext();
                deviceContext.TrackCall(this);
            }
            catch
            {
                this.AIAgentContext?.Dispose();
                this._serviceScope.Dispose();
                this._turnCts.Dispose();
                this._callCts.Dispose();
                this.VoIPRTP.Close("call initialization failed");
                throw;
            }
        }
        public IServiceProvider ServiceProvider => this._serviceScope.ServiceProvider;
        public Task Disposal => this._disposeCompletion.Task;
        public DeviceContext DeviceContext { get;}
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
        public CancellationToken CallToken => this._callToken;
        public CancellationToken Token
        {
            get
            {
                lock (this._turnLock)
                {
                    return this._turnToken;
                }
            }
        }
        public bool IsAgentMediaPaused => Volatile.Read(ref this._agentMediaPaused) != 0;
        public bool IsUserAudioInputPaused => Volatile.Read(ref this._userAudioInputPaused) != 0;
        public bool IsUserAudioProcessing => Interlocked.Read(ref this._userAudioProcessingTurnId) >= 0;
        public bool IsAgentSwitching
        {
            get
            {
                lock (this._agentSessionLock)
                {
                    return this._assistantSwitching;
                }
            }
        }
        public event Action<CancellationToken>? TurnTokenChanged;

        public void Cancel()
        {
            if (!this._callToken.IsCancellationRequested)
            {
                this._callCts.Cancel();
            }
        }

        public void PauseAgentMedia() => Interlocked.Exchange(ref this._agentMediaPaused, 1);
        public void ResumeAgentMedia() => Interlocked.Exchange(ref this._agentMediaPaused, 0);
        public void PauseUserAudioInput() => Interlocked.Exchange(ref this._userAudioInputPaused, 1);
        public void ResumeUserAudioInput() => Interlocked.Exchange(ref this._userAudioInputPaused, 0);
        public void BeginUserAudioProcessing(long turnId) => Interlocked.Exchange(ref this._userAudioProcessingTurnId, turnId);
        public bool CompleteUserAudioProcessing(long turnId) =>
            Interlocked.CompareExchange(ref this._userAudioProcessingTurnId, -1, turnId) == turnId;

        public bool TryBeginHangupAfterReply()
        {
            lock (this._turnLock)
            {
                if (this._callCts.IsCancellationRequested || this._hangupAfterReplyTurnId >= 0)
                {
                    return false;
                }

                this._hangupAfterReplyTurnId = this._turnId;
            }

            this.PauseUserAudioInput();
            return true;
        }

        public bool IsHangupAfterReplyPending(long turnId)
        {
            lock (this._turnLock)
            {
                return this._hangupAfterReplyTurnId == turnId;
            }
        }

        public bool CompleteHangupAfterReply(long turnId)
        {
            lock (this._turnLock)
            {
                if (this._hangupAfterReplyTurnId != turnId)
                {
                    return false;
                }

                this._hangupAfterReplyTurnId = -1;
            }

            if (!this.UserAgent.IsCallActive)
            {
                return false;
            }

            this.MarkEnding();
            this.UserAgent.Hangup();
            return true;
        }

        public Task<bool> BeginPromptPlayback()
        {
            lock (this._promptPlaybackLock)
            {
                if (this._callCts.IsCancellationRequested || this._promptPlaybackCompletion is not null)
                {
                    return Task.FromResult(false);
                }

                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                this._promptPlaybackCompletion = completion;
                return completion.Task;
            }
        }

        public bool IsPromptPlaybackPending
        {
            get
            {
                lock (this._promptPlaybackLock)
                {
                    return this._promptPlaybackCompletion is not null;
                }
            }
        }

        public void CompletePromptPlayback(bool fullyPlayed)
        {
            TaskCompletionSource<bool>? completion;
            lock (this._promptPlaybackLock)
            {
                completion = this._promptPlaybackCompletion;
                this._promptPlaybackCompletion = null;
            }

            completion?.TrySetResult(fullyPlayed);
        }
        public void MarkPlayingPrompt() =>
            this.DeviceContext.MarkPlayingPrompt(this);
        public void MarkEnding() =>
            this.DeviceContext.MarkCallEnding(this);

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

        /// <summary>
        /// Registers a resource whose lifetime is the connected SIP call.
        /// </summary>
        public void RegisterCallOwnedResource(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);

            lock (this._lifetimeLock)
            {
                if (this._disposeRequested)
                {
                    throw new ObjectDisposedException(nameof(ActiveCallContext));
                }

                this._callOwnedResources.Add(resource);
            }
        }

        /// <summary>
        /// Starts an exclusive Agent session switch for this connected call.
        /// </summary>
        public bool TryBeginAssistantSwitch()
        {
            lock (this._agentSessionLock)
            {
                if (this._assistantSwitching ||
                    this._disposeRequested ||
                    this._callEnded ||
                    this._callCts.IsCancellationRequested)
                {
                    return false;
                }

                this._assistantSwitching = true;
            }

            this.PauseAgentMedia();
            this.RestartTurn();
            return true;
        }

        /// <summary>
        /// Replaces the Agent resources while preserving the connected SIP call.
        /// </summary>
        public async Task<bool> TryReplaceAssistantSessionAsync(string targetAssistantNumber)
        {
            if (string.IsNullOrWhiteSpace(targetAssistantNumber))
            {
                return false;
            }

            AssistantConfig? targetAssistant;
            AIAgentContext currentAgent;
            IReadOnlyList<OfflineDialogueTurn> completedOnlineTurns;
            lock (this._agentSessionLock)
            {
                if (!this._assistantSwitching ||
                    this._disposeRequested ||
                    this._callEnded ||
                    this._callCts.IsCancellationRequested ||
                    !this.DeviceContext.AvailableAssistants.TryGetValue(
                        targetAssistantNumber,
                        out targetAssistant) ||
                    targetAssistant is null)
                {
                    return false;
                }

                currentAgent = this.AIAgentContext;
                completedOnlineTurns = currentAgent.GetCompletedOnlineTurns();
            }

            await currentAgent.DisposeAsync();

            lock (this._agentSessionLock)
            {
                if (this._disposeRequested ||
                    this._callEnded ||
                    this._callCts.IsCancellationRequested)
                {
                    return false;
                }

                this.DialedNumber = targetAssistantNumber;
                this.AssistantConfig = targetAssistant;
                this.AIAgentContext = new AIAgentContext(this);
                foreach (OfflineDialogueTurn turn in completedOnlineTurns)
                {
                    this.AIAgentContext.RecordCompletedOnlineTurn(turn);
                }
                return true;
            }
        }

        /// <summary>
        /// Ends an Agent session switch and permits normal Agent processing.
        /// </summary>
        public void CompleteAssistantSwitch()
        {
            lock (this._agentSessionLock)
            {
                this._assistantSwitching = false;
            }
        }

        public void RestartTurn()
        {
            CancellationTokenSource previous;
            CancellationToken nextToken;
            lock (this._turnLock)
            {
                if (this._disposeRequested || this._stopping || this._callToken.IsCancellationRequested)
                {
                    return;
                }
                previous = this._turnCts;
                this._turnCts = new CancellationTokenSource();
                this._turnToken = this._turnCts.Token;
                Interlocked.Increment(ref this._turnId);
                nextToken = this._turnToken;
            }
            try
            {
                previous.Cancel();
            }
            finally
            {
                previous.Dispose();
                this.TurnTokenChanged?.Invoke(nextToken);
            }
        }

        public void Stop()
        {
            CancellationTokenSource turnCts;
            CancellationToken turnToken;
            lock (this._turnLock)
            {
                this._stopping = true;
                turnCts = this._turnCts;
                turnToken = this._turnToken;
            }
            try
            {
                if (!turnToken.IsCancellationRequested)
                {
                    turnCts.Cancel();
                }
            }
            catch (ObjectDisposedException) when (turnToken.IsCancellationRequested)
            {
            }
            finally
            {
                this.Dispose();
            }
        }

        [MemberNotNull(nameof(this.UserAgent))]
        private void CreateSIPUserAgent(SIPTransport sipTransport)
        {
            this.UserAgent = new SIPUserAgent(sipTransport, null, true);
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

            int packetTimeMs = IsSupportedPacketTime(ptime)
                ? ptime!.Value
                : AudioProcessSettings.DefaultPacketTimeMs;

            // maxptime 仅表示上限，不是建议采用的包时长。
            if (IsSupportedPacketTime(maxPtime) && packetTimeMs > maxPtime!.Value)
            {
                packetTimeMs = maxPtime!.Value;
            }
            this.PacketTimeMs = packetTimeMs;
            this.MaxPacketTimeMs = IsSupportedPacketTime(maxPtime)
                ? maxPtime!.Value
                : packetTimeMs;
        }

        private static bool IsSupportedPacketTime(int? packetTimeMs) =>
            packetTimeMs is >= MinimumPacketTimeMs and <= MaximumPacketTimeMs;

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
            if (this.DeviceContext.AvailableAssistants.TryGetValue(this.DialedNumber, out AssistantConfig? assistantConfig) && assistantConfig is not null)
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
            lock (this._lifetimeLock)
            {
                if (this._disposeRequested)
                {
                    return;
                }
                this._disposeRequested = true;
            }

            try
            {
                this.EndCallTransport();
            }
            finally
            {
                lock (this._lifetimeLock)
                {
                    this._transportEnded = true;
                }
                this.TryStartDisposal();
            }
        }

        private void ReleaseUse()
        {
            lock (this._lifetimeLock)
            {
                this._useCount--;
            }
            this.TryStartDisposal();
        }

        private void TryStartDisposal()
        {
            lock (this._lifetimeLock)
            {
                if (!this._disposeRequested || !this._transportEnded || this._useCount != 0 || this._disposeStarted)
                {
                    return;
                }
                this._disposeStarted = true;
            }
            _ = this.DisposeCoreAsync();
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception> errors = [];
            try
            {
                try
                {
                    this._turnCts.Cancel();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
                try
                {
                    await this.AIAgentContext.DisposeAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }

                List<IDisposable> callOwnedResources;
                lock (this._lifetimeLock)
                {
                    callOwnedResources = [.. this._callOwnedResources];
                    this._callOwnedResources.Clear();
                }
                for (int index = callOwnedResources.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        callOwnedResources[index].Dispose();
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
                this._turnCts.Dispose();
                this._callCts.Dispose();
                await this._serviceScope.DisposeAsync();
                if (errors.Count > 0)
                {
                    throw new AggregateException(errors);
                }
                this._disposeCompletion.TrySetResult();
            }
            catch (Exception exception)
            {
                this._disposeCompletion.TrySetException(exception);
            }
        }

        private void EndCallTransport()
        {
            lock (this._turnLock)
            {
                this._hangupAfterReplyTurnId = -1;
            }

            lock (this._lifetimeLock)
            {
                if (this._callEnded)
                {
                    return;
                }

                this._callEnded = true;
            }

            try
            {
                this._callCts.Cancel();
            }
            finally
            {
                this.CompletePromptPlayback(fullyPlayed: false);
                this.VoIPRTP.Close("call ended");
            }
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
