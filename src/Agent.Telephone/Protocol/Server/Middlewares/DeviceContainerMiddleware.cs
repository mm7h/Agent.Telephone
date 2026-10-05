using System.Net.Sockets;
using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Management;
using Agent.Telephone.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Protocol.Server.Middlewares
{
    internal sealed class DeviceContainerMiddleware
    {
        private const int ErrorPromptPlaybackCount = 2;

        private SIPTransport? _sipTransport;

        private readonly IServiceProvider _serviceProvider;
        private readonly TelephoneConfig _config;
        private readonly DeviceContextManager _deviceManager;
        private readonly FunctionToolManager _functionToolManager;
        private readonly HandlerManager _handlerManager;
        private readonly ProviderManager _providerManager;
        private readonly IAudioPromptPlayer _audioPromptPlayer;
        private readonly ILogger<DeviceContainerMiddleware> _logger;

        public DeviceContainerMiddleware(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            DeviceContextManager deviceManager,
            FunctionToolManager functionToolManager,
            HandlerManager handlerManager,
            ProviderManager providerManager,
            IAudioPromptPlayer audioPromptPlayer,
            ILogger<DeviceContainerMiddleware> logger)
        {
            this._serviceProvider = serviceProvider;
            this._config = config;
            this._deviceManager = deviceManager;
            this._functionToolManager = functionToolManager;
            this._handlerManager = handlerManager;
            this._providerManager = providerManager;
            this._audioPromptPlayer = audioPromptPlayer;
            this._logger = logger;
        }

        public void SubscribeSIPTransportEvents(SIPTransport sipTransport)
        {
            this._sipTransport = sipTransport;
            sipTransport.SIPTransportRequestReceived += this.OnRequestReceivedAsync;
        }

        public void UnsubscribeSIPTransportEvents(SIPTransport sipTransport)
        {
            sipTransport.SIPTransportRequestReceived -= this.OnRequestReceivedAsync;
        }

        private async Task OnRequestReceivedAsync(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
        {
            SIPTransport? transport = this._sipTransport;
            if (transport is null)
            {
                return;
            }

            try
            {
                switch (request.Method)
                {
                    case SIPMethodsEnum.REGISTER:
                        (bool registerVerified, string? registerFailureReason) =
                            await this.VerifySIPDeviceAsync(remote, request);
                        if (registerVerified)
                        {
                            await this.RegisterSIPDeviceAsync(transport, request);
                        }
                        else
                        {
                            await SendResponseAsync(
                                transport,
                                request,
                                SIPResponseStatusCodesEnum.Forbidden,
                                registerFailureReason);
                        }
                        break;
                    case SIPMethodsEnum.INVITE:
                        (bool inviteVerified, string? inviteFailureReason) =
                            await this.VerifySIPDeviceAsync(remote, request);
                        if (inviteVerified)
                        {
                            await this.InviteSIPDeviceAsync(transport, request);
                        }
                        else
                        {
                            await this.PlayStandaloneInviteErrorPromptAsync(
                                transport,
                                request,
                                SIPResponseStatusCodesEnum.Forbidden,
                                SIPResponseStatusCodesEnum.Forbidden,
                                inviteFailureReason ?? "Authentication failed",
                                null);
                        }
                        break;
                    case SIPMethodsEnum.OPTIONS:
                        this.RefreshKnownDevice(request);
                        await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.Ok, null);
                        break;
                    case SIPMethodsEnum.BYE:
                        await this.EndCallAsync(transport, request);
                        break;
                    case SIPMethodsEnum.CANCEL:
                        // Matched CANCEL requests are consumed by SIPSorcery's UAS INVITE
                        // transaction and result in 200 (CANCEL) plus 487 (INVITE).
                        await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, "No matching INVITE transaction");
                        break;
                    case SIPMethodsEnum.ACK:
                        break;
                    default:
                        await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.MethodNotAllowed, "Unsupported SIP method");
                        break;
                }
            }
            catch (InvalidOperationException exception)
            {
                this._logger.LogWarning(exception, "拒绝格式错误的 {method} 请求。", request.Method);
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.BadRequest, exception.Message);
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "处理 {method} SIP 请求失败。", request.Method);
                if (request.Method == SIPMethodsEnum.INVITE &&
                    TryHasSupportedAudio(request.Body))
                {
                    await this.PlayStandaloneInviteErrorPromptAsync(
                        transport,
                        request,
                        SIPResponseStatusCodesEnum.ServiceUnavailable,
                        SIPResponseStatusCodesEnum.InternalServerError,
                        "SIP request processing failed",
                        null);
                }
                else
                {
                    await SendResponseAsync(
                        transport,
                        request,
                        SIPResponseStatusCodesEnum.InternalServerError,
                        "SIP request processing failed");
                }
            }
        }

        #region Verify
        private Task<(bool Succeeded, string? FailureReason)> VerifySIPDeviceAsync(
            SIPEndPoint remote,
            SIPRequest request)
        {
            SIPURI callerAor = request.GetCallerAor();
            if (!this._config.AuthEnabled)
            {
                return Task.FromResult((true, (string?)null));
            }

            IBasicVerify? verifier = this._serviceProvider.GetService<IBasicVerify>();
            if (verifier is null)
            {
                this._logger.LogError("已启用 SIP 认证，但未注册 {verifyType}。", nameof(IBasicVerify));
                return Task.FromResult((false, (string?)"Authentication unavailable"));
            }

            try
            {
                var remoteEndPoint = remote.GetIPEndPoint();
                if (verifier.Verify(callerAor.User, remoteEndPoint))
                {
                    return Task.FromResult((true, (string?)null));
                }

                this._logger.LogWarning("设备 {dialingNumber} 未通过 SIP 认证，来源 {remoteEndPoint}。", callerAor.User, remoteEndPoint);
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "验证设备 {dialingNumber} 的 SIP 请求时发生异常。", callerAor.User);
            }

            return Task.FromResult((false, (string?)"Authentication failed"));
        } 
        #endregion

        #region REGISTER
        private async Task RegisterSIPDeviceAsync(SIPTransport transport, SIPRequest request)
        {
            (SIPURI contact, int expiresSeconds) = request.GetRegistration();
            await this._deviceManager.OnSIPDeviceRegisteringAsync(transport, request);

            SIPResponse response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
            response.Header.Contact = request.Header.Contact;
            response.Header.Expires = expiresSeconds;
            await transport.SendResponseAsync(response);

            if (expiresSeconds == 0)
            {
                this._logger.LogInformation("设备 {deviceId} 已注销。", request.GetDeviceId());
            }
            else
            {
                this._logger.LogInformation("设备 {deviceId} 已注册，Contact={contact}，有效期={expiresSeconds}秒。",
                    request.GetDeviceId(),
                    contact,
                    expiresSeconds);
            }
        }
        #endregion

        #region INVITE
        private async Task InviteSIPDeviceAsync(SIPTransport transport, SIPRequest request)
        {
            DeviceContext? device = this._deviceManager.GetRegisteredSIPDeviceById(request);
            if (device is null)
            {
                this._logger.LogError("设备 {deviceId} 的 INVITE 请求未注册。", request.GetDeviceId());
                await this.PlayStandaloneInviteErrorPromptAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.Forbidden,
                    SIPResponseStatusCodesEnum.Forbidden,
                    "Device is not registered",
                    null);
                return;
            }

            device.RefreshLastActivityTime();

            bool hasSupportedAudio;
            try
            {
                hasSupportedAudio = HasSupportedAudio(request.Body);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("INVITE 包含无效的 SDP。", exception);
            }

            if (!hasSupportedAudio)
            {
                this._logger.LogError("设备 {deviceId} 的 INVITE 不包含受支持的音频格式。", device.DeviceId);
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.NotAcceptableHere, "PCMU or PCMA audio is required");
                return;
            }

            string assistantNumber = request.GetAssistantNumber();
            if (!device.AvailableAssistants.ContainsKey(assistantNumber))
            {
                this._logger.LogError("设备 {deviceId} 的 INVITE 请求中指定的助手号码 {assistantNumber} 不存在。", device.DeviceId, assistantNumber);
                await this.PlayStandaloneInviteErrorPromptAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.NotFound,
                    SIPResponseStatusCodesEnum.NotFound,
                    "Assistant number not found",
                    device);
                return;
            }

            if (!device.TryInitializeCallSession(request, out ActiveCallContext? activeCall) || activeCall is null)
            {
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.BusyHere, "Device already has an active call");
                return;
            }

            try
            {
                int timeoutSeconds = Math.Max(10, this._config.SIPConfig.AgentInitializationTimeoutSeconds);

                bool toolsBuilt = await this._functionToolManager.OnSIPDeviceRegisteredAsync(device, transport, request).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

                if (!toolsBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    this._logger.LogError("设备 {deviceId} 的 FunctionTools 初始化失败。", device.DeviceId);
                    await this.PlayActiveCallErrorPromptAndEndAsync(
                        device,
                        activeCall,
                        SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                        "FunctionTool initialization failed");
                    return;
                }

                bool providersBuilt = await this._providerManager
                    .OnSIPDeviceRegisteredAsync(device, transport, request)
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

                if (!providersBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    this._logger.LogError("设备 {deviceId} 的 Providers 初始化失败。", device.DeviceId);
                    await this.PlayActiveCallErrorPromptAndEndAsync(
                        device,
                        activeCall,
                        SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                        "Agent provider initialization failed");
                    return;
                }

                bool handlersBuilt = await this._handlerManager.OnSIPDeviceRegisteredAsync(device, transport, request).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

                if (!handlersBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    this._logger.LogError("设备 {deviceId} 的 Handlers 初始化失败。", device.DeviceId);
                    await this.PlayActiveCallErrorPromptAndEndAsync(
                        device,
                        activeCall,
                        SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                        "Audio pipeline unavailable");
                    return;
                }


                this._logger.LogInformation("已接听来自号码：{callerNumber} 的呼叫，拨号号码：{dialedNumber}。", activeCall.CallerNumber, activeCall.DialedNumber);
                return;

            }
            catch (TimeoutException)
            {
                this._logger.LogWarning("设备 {deviceId} 的 Agent 初始化超过 {timeoutSeconds} 秒。", device.DeviceId, this._config.SIPConfig.AgentInitializationTimeoutSeconds);
                await this.PlayActiveCallErrorPromptAndEndAsync(
                    device,
                    activeCall,
                    SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                    "Agent initialization timed out");
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "设备 {deviceId} 的 Agent 初始化失败。", device.DeviceId);
                await this.PlayActiveCallErrorPromptAndEndAsync(
                    device,
                    activeCall,
                    SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                    "Agent initialization failed");
            }
        }
        #endregion

        #region BYE
        private async Task EndCallAsync(SIPTransport transport, SIPRequest request)
        {
            DeviceContext? device = this._deviceManager.GetSIPDeviceById(request);
            this._logger.LogInformation("收到设备 {deviceId} 的 BYE，关闭 Call-ID {callId}。", device?.DeviceId ?? "unknown", request.Header.CallId);
            device?.RefreshLastActivityTime();
            device?.CloseCallSession();
            await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.Ok, null);
        }
        #endregion

        #region Support Methods
        private void RefreshKnownDevice(SIPRequest request)
        {
            try
            {
                this._deviceManager.GetSIPDeviceById(request)?.RefreshLastActivityTime();
            }
            catch (InvalidOperationException)
            {
                // Server-targeted OPTIONS can legitimately omit a device AOR.
            }
        }
        private static bool HasSupportedAudio(string? sdpBody)
        {
            if (string.IsNullOrWhiteSpace(sdpBody))
            {
                return false;
            }

            SDP sdp = SDP.ParseSDPDescription(sdpBody);
            SDPMediaAnnouncement? audio = sdp.Media.FirstOrDefault(media => media.Media == SDPMediaTypesEnum.audio);

            return audio?.MediaFormats.Values.Any(format =>
                string.Equals(format.Name(), nameof(SDPWellKnownMediaFormatsEnum.PCMU), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format.Name(), nameof(SDPWellKnownMediaFormatsEnum.PCMA), StringComparison.OrdinalIgnoreCase)) == true;
        }

        private async Task PlayStandaloneInviteErrorPromptAsync(
            SIPTransport transport,
            SIPRequest request,
            SIPResponseStatusCodesEnum promptCode,
            SIPResponseStatusCodesEnum fallbackStatus,
            string reason,
            DeviceContext? device)
        {
            if (!TryHasSupportedAudio(request.Body))
            {
                await SendResponseAsync(transport, request, fallbackStatus, reason);
                return;
            }

            if (device is not null && !device.TryBeginErrorPrompt())
            {
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.BusyHere,
                    "Device already has an active call");
                return;
            }

            SIPUserAgent userAgent = new(transport, null, true);
            VoIPMediaSession mediaSession = CreatePromptMediaSession();
            using CancellationTokenSource playbackCts = new();
            SIPServerUserAgent? serverUserAgent = null;
            bool answered = false;

            void OnCallHungup(SIPDialogue _)
            {
                playbackCts.Cancel();
            }

            void OnServerCallCancelled(ISIPServerUserAgent serverAgent, SIPRequest cancelledRequest)
            {
                playbackCts.Cancel();
            }

            userAgent.OnCallHungup += OnCallHungup;
            userAgent.ServerCallCancelled += OnServerCallCancelled;
            try
            {
                serverUserAgent = userAgent.AcceptCall(request);
                if (playbackCts.IsCancellationRequested)
                {
                    return;
                }

                if (!await userAgent.Answer(serverUserAgent, mediaSession))
                {
                    serverUserAgent.Reject(fallbackStatus, reason);
                    return;
                }

                answered = true;
                AudioFormat audioFormat = mediaSession.AudioStream.GetSendingFormat().ToAudioFormat();
                if (audioFormat.IsEmpty())
                {
                    this._logger.LogWarning("错误提示音 {SipCode} 未协商到可发送的音频格式。", (int)promptCode);
                    return;
                }

                for (int playbackIndex = 0;
                    playbackIndex < ErrorPromptPlaybackCount && !playbackCts.IsCancellationRequested;
                    playbackIndex++)
                {
                    if (!await this._audioPromptPlayer.PlaySIPCodeAudioAsync(
                            promptCode,
                            mediaSession,
                            audioFormat,
                            AudioProcessSettings.DefaultPacketTimeMs,
                            playbackCts.Token))
                    {
                        this._logger.LogWarning(
                            "错误提示音 {SipCode} 第 {PlaybackIndex} 次播放失败。",
                            (int)promptCode,
                            playbackIndex + 1);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (playbackCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(exception, "播放 SIP 错误提示音 {SipCode} 时发生异常。", (int)promptCode);
                if (!answered && !playbackCts.IsCancellationRequested)
                {
                    if (serverUserAgent is not null)
                    {
                        serverUserAgent.Reject(fallbackStatus, reason);
                    }
                    else
                    {
                        await SendResponseAsync(transport, request, fallbackStatus, reason);
                    }
                }
            }
            finally
            {
                userAgent.OnCallHungup -= OnCallHungup;
                userAgent.ServerCallCancelled -= OnServerCallCancelled;
                if (answered && userAgent.IsCallActive)
                {
                    userAgent.Hangup();
                }

                mediaSession.Close("error prompt completed");
                device?.EndErrorPrompt();
            }
        }

        private async Task PlayActiveCallErrorPromptAndEndAsync(
            DeviceContext device,
            ActiveCallContext activeCall,
            SIPResponseStatusCodesEnum promptCode,
            string reason)
        {
            activeCall.PauseAgentMedia();
            activeCall.PauseUserAudioInput();
            activeCall.RestartTurn();
            activeCall.Cancel();

            using CancellationTokenSource playbackCts = new();
            void OnCallHungup(SIPDialogue _)
            {
                playbackCts.Cancel();
            }

            void OnServerCallCancelled(ISIPServerUserAgent serverAgent, SIPRequest cancelledRequest)
            {
                playbackCts.Cancel();
            }

            activeCall.UserAgent.OnCallHungup += OnCallHungup;
            activeCall.UserAgent.ServerCallCancelled += OnServerCallCancelled;
            try
            {
                if (!activeCall.UserAgent.IsCallActive)
                {
                    SIPServerUserAgent? serverUserAgent = device.TakePendingServerUserAgent(activeCall);
                    if (serverUserAgent is null || playbackCts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!await activeCall.UserAgent.Answer(serverUserAgent, activeCall.VoIPRTP))
                    {
                        serverUserAgent.Reject(promptCode, reason);
                        return;
                    }

                    activeCall.NegotiatedAudioFormat = activeCall.VoIPRTP
                        .AudioStream
                        .GetSendingFormat()
                        .ToAudioFormat();
                }

                AudioFormat audioFormat = activeCall.NegotiatedAudioFormat;
                if (audioFormat.IsEmpty())
                {
                    audioFormat = activeCall.VoIPRTP.AudioStream.GetSendingFormat().ToAudioFormat();
                    activeCall.NegotiatedAudioFormat = audioFormat;
                }

                if (audioFormat.IsEmpty())
                {
                    this._logger.LogWarning("通话 {CallId} 未协商到错误提示音所需的音频格式。", activeCall.CallId);
                    return;
                }

                device.MarkPlayingPrompt(activeCall);
                for (int playbackIndex = 0;
                    playbackIndex < ErrorPromptPlaybackCount && !playbackCts.IsCancellationRequested;
                    playbackIndex++)
                {
                    if (!await this._audioPromptPlayer.PlaySIPCodeAudioAsync(
                            promptCode,
                            activeCall.VoIPRTP,
                            audioFormat,
                            activeCall.PacketTimeMs,
                            playbackCts.Token))
                    {
                        this._logger.LogWarning(
                            "通话 {CallId} 的错误提示音 {SipCode} 第 {PlaybackIndex} 次播放失败。",
                            activeCall.CallId,
                            (int)promptCode,
                            playbackIndex + 1);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (playbackCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(
                    exception,
                    "播放通话 {CallId} 的 SIP 错误提示音 {SipCode} 时发生异常。",
                    activeCall.CallId,
                    (int)promptCode);
            }
            finally
            {
                activeCall.UserAgent.OnCallHungup -= OnCallHungup;
                activeCall.UserAgent.ServerCallCancelled -= OnServerCallCancelled;
                if (activeCall.UserAgent.IsCallActive)
                {
                    device.MarkCallEnding(activeCall);
                    activeCall.UserAgent.Hangup();
                }

                device.CloseCallSession(activeCall);
            }
        }

        private static VoIPMediaSession CreatePromptMediaSession()
        {
            AudioEncoder audioEncoder = new(SupportedAudioFormats.SupportedSDPAudioFormat);
            AudioExtrasSource source = new(
                audioEncoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
            source.RestrictFormats(format => SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));
            return new VoIPMediaSession(new MediaEndPoints { AudioSource = source })
            {
                AcceptRtpFromAny = true
            };
        }

        private static bool TryHasSupportedAudio(string? sdpBody)
        {
            try
            {
                return HasSupportedAudio(sdpBody);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Task<SocketError> SendResponseAsync(SIPTransport transport, SIPRequest request, SIPResponseStatusCodesEnum status, string? reason)
        {
            return transport.SendResponseAsync(SIPResponse.GetResponse(request, status, reason));
        } 
        #endregion
    }
}
