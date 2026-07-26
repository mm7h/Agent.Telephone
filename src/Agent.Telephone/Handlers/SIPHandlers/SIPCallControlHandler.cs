using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.CallControl;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Handlers.SIPHandlers
{
    /// <summary>
    /// Owns outbound SIP legs, prompt media and RTP bridging for one active call.
    /// CallControlProvider supplies only validated transfer commands.
    /// </summary>
    internal sealed class SIPCallControlHandler : BaseHandler, ICallTransferExecutor
    {
        private readonly SIPTransport _sipTransport;
        private readonly SemaphoreSlim _transferLock = new(1, 1);
        private ActiveCallContext? _call;
        private IAudioProcessor? _audioProcessor;
        private CancellationTokenSource? _transferCts;
        private CancellationTokenSource? _waitingPromptCts;
        private Task? _waitingPromptTask;
        private SIPUserAgent? _outboundUserAgent;
        private VoIPMediaSession? _outboundMedia;
        private MediaBridge? _mediaBridge;
        private IRegisteredEndpointLease? _targetLease;
        private int _ending;

        public SIPCallControlHandler(
            SIPTransport sipTransport,
            TelephoneConfig config,
            ILogger<SIPCallControlHandler> logger)
            : base(config, logger)
        {
            this._sipTransport = sipTransport;
        }

        public override string HandlerName => nameof(SIPCallControlHandler);

        public override bool Build()
        {
            ActiveCallContext? activeCall = this.DeviceContext.ActiveCall;
            IAudioProcessor? audioProcessor = activeCall?.AIAgentContext.PrivateProvider.AudioProcessor;
            if (activeCall is null || audioProcessor is null)
            {
                this.Logger.LogWarning(
                    "设备 {DeviceId} 缺少通话控制所需的活动呼叫或音频 Provider。",
                    this.DeviceContext.DeviceId);
                return false;
            }

            this._call = activeCall;
            this._audioProcessor = audioProcessor;
            this._transferCts = CancellationTokenSource.CreateLinkedTokenSource(
                activeCall.CallToken);
            activeCall.AIAgentContext.PrivateProvider.SetCallTransferExecutor(this);
            activeCall.UserAgent.OnCallHungup += this.OnEitherLegHungup;
            activeCall.VoIPRTP.OnTimeout += this.OnMediaTimeout;
            return true;
        }

        public async Task<CallTransferResult> TransferAsync(
            CallTransferCommand command,
            CancellationToken cancellationToken)
        {
            bool transferLockAcquired = false;
            try
            {
                await this._transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                transferLockAcquired = true;

                ActiveCallContext? call = this._call;
                if (!ReferenceEquals(call, command.Call) ||
                    call is null ||
                    this._transferCts?.IsCancellationRequested != false ||
                    !call.UserAgent.IsCallActive)
                {
                    command.Lease.Dispose();
                    return new CallTransferResult(
                        CallTransferStatus.CallEnded,
                        command.Endpoint.DialingNumber,
                        "原通话已经结束。");
                }

                if (this._mediaBridge is not null)
                {
                    command.Lease.Dispose();
                    return new CallTransferResult(
                        CallTransferStatus.Failed,
                        command.Endpoint.DialingNumber,
                        "当前通话已经完成转接。");
                }

                using CancellationTokenSource attemptCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        this._transferCts.Token,
                        cancellationToken);
                CancellationToken transferToken = attemptCts.Token;
                call.MarkTransferDialing();
                call.PauseAgentMedia();
                this.StartWaitingPrompt(call, transferToken);

                CallTransferResult result = await this.DialAndBridgeAsync(
                    command,
                    transferToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    command.Lease.Dispose();
                    await this.PlayFailureAsync(call, result, transferToken)
                        .ConfigureAwait(false);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                command.Lease.Dispose();
                this.StopWaitingPrompt();
                return new CallTransferResult(
                    CallTransferStatus.CallEnded,
                    command.Endpoint.DialingNumber,
                    "转接已取消。");
            }
            finally
            {
                if (transferLockAcquired)
                {
                    this._transferLock.Release();
                }
            }
        }

        public async Task PlayFailureAsync(
            ActiveCallContext call,
            CallTransferResult result,
            CancellationToken cancellationToken)
        {
            this.StopWaitingPrompt();
            call.PauseAgentMedia();
            call.MarkPlayingPrompt();
            try
            {
                IAudioProcessor? audioProcessor = this._audioProcessor;
                if (audioProcessor is not null)
                {
                    using CancellationTokenSource promptCts =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            call.CallToken);
                    await audioProcessor.PlayFileAsync(
                        this.Config.PromptMediaConfig.TransferFailed,
                        call.VoIPRTP,
                        call.NegotiatedAudioFormat,
                        promptCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                this.Logger.LogWarning(
                    exception,
                    "播放转接失败提示音时发生异常，目标号码 {TargetNumber}。",
                    result.TargetNumber);
            }
            finally
            {
                call.MarkEnding();
                try
                {
                    if (call.UserAgent.IsCallActive)
                    {
                        call.UserAgent.Hangup();
                    }
                }
                catch (Exception exception)
                {
                    this.Logger.LogDebug(exception, "结束转接失败的原通话时发生异常。");
                }
            }
        }

        private async Task<CallTransferResult> DialAndBridgeAsync(
            CallTransferCommand command,
            CancellationToken cancellationToken)
        {
            ActiveCallContext call = command.Call;
            AudioFormat outboundFormat = AudioFormat.Empty;
            SIPResponse? failureResponse = null;
            string? failureReason = null;
            bool bridgeEstablished = false;

            AudioEncoder encoder = new(SupportedAudioFormats.SupportedSDPAudioFormat);
            AudioExtrasSource source = new(
                encoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
            source.RestrictFormats(format => format.Codec == command.Codec);
            VoIPMediaSession outboundMedia = new(
                new MediaEndPoints { AudioSource = source })
            {
                AcceptRtpFromAny = true
            };
            outboundMedia.OnAudioFormatsNegotiated += formats =>
            {
                outboundFormat = formats.FirstOrDefault(format =>
                    format.Codec == command.Codec);
            };

            SIPUserAgent outboundUserAgent = new(
                this._sipTransport,
                SIPEndPoint.Empty,
                false);
            void OnCallFailed(
                ISIPClientUserAgent userAgent,
                string errorMessage,
                SIPResponse response)
            {
                failureReason = errorMessage;
                failureResponse = response;
            }

            outboundUserAgent.ClientCallFailed += OnCallFailed;
            this._outboundUserAgent = outboundUserAgent;
            this._outboundMedia = outboundMedia;
            try
            {
                SIPURI sourceAor = SIPURI.ParseSIPURI(call.UserAor);
                SIPCallDescriptor descriptor = new(command.Endpoint.ContactUri, string.Empty);
                descriptor.SetGeneralFromHeaderFields(
                    null,
                    call.CallerNumber ?? string.Empty,
                    sourceAor.Host);
                using CancellationTokenRegistration registration =
                    cancellationToken.Register(outboundUserAgent.Cancel);
                bool answered = await outboundUserAgent.Call(
                    descriptor,
                    outboundMedia,
                    ringTimeout: command.RingTimeoutSeconds).ConfigureAwait(false);
                if (!answered)
                {
                    return CallTransferPolicy.MapFailure(
                        command.Endpoint.DialingNumber,
                        failureResponse?.Status,
                        failureReason,
                        cancellationToken.IsCancellationRequested ||
                            !call.UserAgent.IsCallActive);
                }

                if (outboundFormat.IsEmpty() || outboundFormat.Codec != command.Codec)
                {
                    outboundUserAgent.Hangup();
                    return new CallTransferResult(
                        CallTransferStatus.CodecNotSupported,
                        command.Endpoint.DialingNumber,
                        "目标设备未接受原通话的音频编码。");
                }

                this.StopWaitingPrompt();
                call.MarkBridged();
                this._mediaBridge = new MediaBridge(
                    call.VoIPRTP,
                    call.NegotiatedAudioFormat.FormatID,
                    outboundMedia,
                    outboundFormat.FormatID,
                    (uint)Math.Max(1, call.PacketTimeMs * 8),
                    this.Logger);
                this._targetLease = command.Lease;
                bridgeEstablished = true;
                outboundUserAgent.OnCallHungup += this.OnEitherLegHungup;
                outboundMedia.OnTimeout += this.OnMediaTimeout;
                return new CallTransferResult(
                    CallTransferStatus.Success,
                    command.Endpoint.DialingNumber);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CallTransferResult(
                    CallTransferStatus.CallEnded,
                    command.Endpoint.DialingNumber,
                    "转接已取消。");
            }
            catch (Exception exception)
            {
                this.Logger.LogError(
                    exception,
                    "转接到号码 {TargetNumber} 时发生异常。",
                    command.Endpoint.DialingNumber);
                return new CallTransferResult(
                    CallTransferStatus.Failed,
                    command.Endpoint.DialingNumber,
                    exception.Message);
            }
            finally
            {
                outboundUserAgent.ClientCallFailed -= OnCallFailed;
                if (!bridgeEstablished)
                {
                    try
                    {
                        if (outboundUserAgent.IsCallActive)
                        {
                            outboundUserAgent.Hangup();
                        }
                        else
                        {
                            outboundUserAgent.Cancel();
                        }
                    }
                    catch (Exception exception)
                    {
                        this.Logger.LogDebug(exception, "清理未建立的转接呼叫时发生异常。");
                    }
                    outboundMedia.Close("call transfer attempt ended");
                    if (ReferenceEquals(this._outboundUserAgent, outboundUserAgent))
                    {
                        this._outboundUserAgent = null;
                        this._outboundMedia = null;
                    }
                }
            }
        }

        private void StartWaitingPrompt(
            ActiveCallContext call,
            CancellationToken transferToken)
        {
            this.StopWaitingPrompt();
            if (string.IsNullOrWhiteSpace(this.Config.PromptMediaConfig.TransferWaiting) ||
                this._audioProcessor is null)
            {
                return;
            }

            this._waitingPromptCts = CancellationTokenSource.CreateLinkedTokenSource(
                transferToken);
            CancellationToken promptToken = this._waitingPromptCts.Token;
            this._waitingPromptTask = Task.Run(async () =>
            {
                try
                {
                    while (!promptToken.IsCancellationRequested && call.UserAgent.IsCallActive)
                    {
                        bool played = await this._audioProcessor.PlayFileAsync(
                            this.Config.PromptMediaConfig.TransferWaiting,
                            call.VoIPRTP,
                            call.NegotiatedAudioFormat,
                            promptToken).ConfigureAwait(false);
                        if (!played)
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (promptToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    this.Logger.LogWarning(exception, "播放转接等待音时发生异常。");
                }
            }, CancellationToken.None);
        }

        private void StopWaitingPrompt()
        {
            CancellationTokenSource? cts = Interlocked.Exchange(
                ref this._waitingPromptCts,
                null);
            cts?.Cancel();
            cts?.Dispose();
            this._waitingPromptTask = null;
        }

        private void OnEitherLegHungup(SIPDialogue dialogue) => this.EndBothLegs();

        private void OnMediaTimeout(SDPMediaTypesEnum mediaType)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                this.EndBothLegs();
            }
        }

        private void EndBothLegs()
        {
            if (Interlocked.Exchange(ref this._ending, 1) != 0)
            {
                return;
            }

            this.StopWaitingPrompt();
            this._call?.MarkEnding();
            this._mediaBridge?.Dispose();
            this._mediaBridge = null;
            try
            {
                if (this._outboundUserAgent?.IsCallActive == true)
                {
                    this._outboundUserAgent.Hangup();
                }
                else
                {
                    this._outboundUserAgent?.Cancel();
                }
            }
            catch (Exception exception)
            {
                this.Logger.LogDebug(exception, "结束转接目标通话时发生异常。");
            }
            try
            {
                if (this._call?.UserAgent.IsCallActive == true)
                {
                    this._call.UserAgent.Hangup();
                }
            }
            catch (Exception exception)
            {
                this.Logger.LogDebug(exception, "结束转接原通话时发生异常。");
            }
            Interlocked.Exchange(ref this._targetLease, null)?.Dispose();
        }

        public override void Dispose()
        {
            this._transferCts?.Cancel();
            this.EndBothLegs();
            ActiveCallContext? call = this._call;
            if (call is not null)
            {
                call.UserAgent.OnCallHungup -= this.OnEitherLegHungup;
                call.VoIPRTP.OnTimeout -= this.OnMediaTimeout;
                call.AIAgentContext.PrivateProvider.ClearCallTransferExecutor(this);
            }
            if (this._outboundUserAgent is not null)
            {
                this._outboundUserAgent.OnCallHungup -= this.OnEitherLegHungup;
            }
            if (this._outboundMedia is not null)
            {
                this._outboundMedia.OnTimeout -= this.OnMediaTimeout;
                this._outboundMedia.Close("call transfer ended");
            }
            this._transferCts?.Dispose();
            this._transferLock.Dispose();
            base.Dispose();
        }
    }
}
