using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Management;
using Agent.Telephone.Providers.Conversation;
using Agent.Telephone.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;
using System.Net.Sockets;

namespace Agent.Telephone.Protocol.Server.Middlewares
{
    internal sealed class DeviceContainerMiddleware
    {
        private SIPTransport? _sipTransport;

        private readonly IServiceProvider _serviceProvider;
        private readonly TelephoneConfig _config;
        private readonly DeviceContextManager _deviceManager;
        private readonly FunctionToolManager _functionToolManager;
        private readonly HandlerManager _handlerManager;
        private readonly ProviderManager _providerManager;
        private readonly ConversationProvider _conversationProvider;
        private readonly ILogger<DeviceContainerMiddleware> _logger;

        public DeviceContainerMiddleware(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            DeviceContextManager deviceManager,
            FunctionToolManager functionToolManager,
            HandlerManager handlerManager,
            ProviderManager providerManager,
            ConversationProvider conversationProvider,
            ILogger<DeviceContainerMiddleware> logger)
        {
            this._serviceProvider = serviceProvider;
            this._config = config;
            this._deviceManager = deviceManager;
            this._functionToolManager = functionToolManager;
            this._handlerManager = handlerManager;
            this._providerManager = providerManager;
            this._conversationProvider = conversationProvider;
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
                        if (await this.VerifySIPDeviceAsync(transport, remote, request))
                        {
                            await this.RegisterSIPDeviceAsync(transport, request);
                        }
                        break;
                    case SIPMethodsEnum.INVITE:
                        if (await this.VerifySIPDeviceAsync(transport, remote, request))
                        {
                            await this.InviteSIPDeviceAsync(transport, request);
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
                        await SendResponseAsync(
                            transport,
                            request,
                            SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist,
                            "No matching INVITE transaction");
                        break;
                    case SIPMethodsEnum.ACK:
                        break;
                    default:
                        await SendResponseAsync(
                            transport,
                            request,
                            SIPResponseStatusCodesEnum.MethodNotAllowed,
                            "Unsupported SIP method");
                        break;
                }
            }
            catch (InvalidOperationException exception)
            {
                this._logger.LogWarning(exception, "拒绝格式错误的 {method} 请求。", request.Method);
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.BadRequest,
                    exception.Message);
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "处理 {method} SIP 请求失败。", request.Method);
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.InternalServerError,
                    "SIP request processing failed");
            }
        }

        private async Task<bool> VerifySIPDeviceAsync(
            SIPTransport transport,
            SIPEndPoint remote,
            SIPRequest request)
        {
            SIPURI callerAor = request.GetCallerAor();
            if (!this._config.AuthEnabled)
            {
                return true;
            }

            IBasicVerify? verifier = this._serviceProvider.GetService<IBasicVerify>();
            if (verifier is null)
            {
                this._logger.LogError("已启用 SIP 认证，但未注册 {verifyType}。", nameof(IBasicVerify));
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.Forbidden,
                    "Authentication unavailable");
                return false;
            }

            try
            {
                var remoteEndPoint = remote.GetIPEndPoint();
                if (verifier.Verify(callerAor.User, remoteEndPoint))
                {
                    return true;
                }

                this._logger.LogWarning(
                    "设备 {dialingNumber} 未通过 SIP 认证，来源 {remoteEndPoint}。",
                    callerAor.User,
                    remoteEndPoint);
            }
            catch (Exception exception)
            {
                this._logger.LogError(
                    exception,
                    "验证设备 {dialingNumber} 的 SIP 请求时发生异常。",
                    callerAor.User);
            }

            await SendResponseAsync(
                transport,
                request,
                SIPResponseStatusCodesEnum.Forbidden,
                "Authentication failed");
            return false;
        }

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
                this._logger.LogInformation(
                    "设备 {deviceId} 已注册，Contact={contact}，有效期={expiresSeconds}秒。",
                    request.GetDeviceId(),
                    contact,
                    expiresSeconds);
            }
        }

        private async Task InviteSIPDeviceAsync(SIPTransport transport, SIPRequest request)
        {
            DeviceContext? device = this._deviceManager.GetRegisteredSIPDeviceById(request);
            if (device is null)
            {
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.Forbidden,
                    "Device is not registered");
                return;
            }

            device.RefreshLastActivityTime();
            string assistantNumber = request.GetAssistantNumber();
            if (!device.AvailableAssistants.ContainsKey(assistantNumber))
            {
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.NotFound,
                    "Assistant number not found");
                return;
            }

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
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.NotAcceptableHere,
                    "PCMU or PCMA audio is required");
                return;
            }

            if (device.TryGetActiveRegistration(out RegistrationBinding? registration) &&
                registration is not null &&
                device.ActiveCall is null &&
                this._conversationProvider.IsRunning(
                    registration.Aor.ToString(),
                    assistantNumber))
            {
                await this.AnswerBusyTurnAsync(device, request).ConfigureAwait(false);
                return;
            }

            if (!device.TryInitializeCallSession(request, out ActiveCallContext? activeCall) ||
                activeCall is null)
            {
                await SendResponseAsync(
                    transport,
                    request,
                    SIPResponseStatusCodesEnum.BusyHere,
                    "Device already has an active call");
                return;
            }

            try
            {
                int timeoutSeconds = Math.Max(1, this._config.SIPConfig.AgentInitializationTimeoutSeconds);
                DateTimeOffset initializationDeadline = DateTimeOffset.Now.AddSeconds(timeoutSeconds);
                bool toolsBuilt = await this._functionToolManager
                    .OnSIPDeviceRegisteredAsync(device, transport, request)
                    .WaitAsync(GetRemainingTime(initializationDeadline));

                if (!toolsBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    device.RejectPendingCall(
                        activeCall,
                        SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                        "FunctionTool initialization failed");
                    return;
                }

                bool providersBuilt = await this._providerManager
                    .OnSIPDeviceRegisteredAsync(device, transport, request)
                    .WaitAsync(GetRemainingTime(initializationDeadline));

                if (!providersBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    device.RejectPendingCall(
                        activeCall,
                        SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                        "Agent provider initialization failed");
                    return;
                }

                bool handlersBuilt = await this._handlerManager
                    .OnSIPDeviceRegisteredAsync(device, transport, request)
                    .WaitAsync(GetRemainingTime(initializationDeadline));

                if (handlersBuilt && ReferenceEquals(device.ActiveCall, activeCall))
                {
                    this._logger.LogInformation(
                        "已接听来自号码：{callerNumber} 的呼叫，拨号号码：{dialedNumber}。",
                        activeCall.CallerNumber,
                        activeCall.DialedNumber);
                    return;
                }

                device.RejectPendingCall(
                    activeCall,
                    SIPResponseStatusCodesEnum.NotAcceptableHere,
                    "Audio pipeline unavailable");
            }
            catch (TimeoutException)
            {
                this._logger.LogWarning(
                    "设备 {deviceId} 的 Agent 初始化超过 {timeoutSeconds} 秒。",
                    device.DeviceId,
                    this._config.SIPConfig.AgentInitializationTimeoutSeconds);
                device.RejectPendingCall(
                    activeCall,
                    SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                    "Agent initialization timed out");
            }
            catch (Exception exception)
            {
                device.RejectPendingCall(
                    activeCall,
                    SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                    "Agent initialization failed");
                this._logger.LogError(
                    exception,
                    "设备 {deviceId} 的 Agent 初始化失败。",
                    device.DeviceId);
            }
        }

        private async Task AnswerBusyTurnAsync(DeviceContext device, SIPRequest request)
        {
            if (!device.TryInitializeCallSession(request, out ActiveCallContext? activeCall) ||
                activeCall is null)
            {
                await SendResponseAsync(
                    this._sipTransport!,
                    request,
                    SIPResponseStatusCodesEnum.BusyHere,
                    "Device already has an active call").ConfigureAwait(false);
                return;
            }

            void OnAudioFormatsNegotiated(List<AudioFormat> formats)
            {
                activeCall.NegotiatedAudioFormat = formats.FirstOrDefault(format =>
                    SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));
            }

            activeCall.VoIPRTP.OnAudioFormatsNegotiated += OnAudioFormatsNegotiated;
            try
            {
                var serverAgent = device.TakePendingServerUserAgent(activeCall);
                if (serverAgent is null ||
                    !await activeCall.UserAgent.Answer(serverAgent, activeCall.VoIPRTP)
                        .ConfigureAwait(false) ||
                    activeCall.NegotiatedAudioFormat.IsEmpty())
                {
                    device.CloseCallSession(activeCall);
                    return;
                }

                device.MarkPlayingPrompt(activeCall);
                IAudioEditor? audioEditor = this._serviceProvider
                    .GetService<IAudioEditor>();
                if (audioEditor is not null)
                {
                    await audioEditor.PlaySIPCodeAudioAsync(
                        SIPResponseStatusCodesEnum.BusyHere,
                        activeCall.VoIPRTP,
                        activeCall.NegotiatedAudioFormat,
                        activeCall.CallToken).ConfigureAwait(false);
                }
            }
            finally
            {
                activeCall.VoIPRTP.OnAudioFormatsNegotiated -= OnAudioFormatsNegotiated;
                try
                {
                    if (activeCall.UserAgent.IsCallActive)
                    {
                        activeCall.UserAgent.Hangup();
                    }
                }
                catch (Exception exception)
                {
                    this._logger.LogDebug(exception, "结束后台任务忙线提示通话时发生异常。");
                }
                device.CloseCallSession(activeCall);
            }
        }

        private async Task EndCallAsync(SIPTransport transport, SIPRequest request)
        {
            DeviceContext? device = this._deviceManager.GetSIPDeviceById(request);
            device?.RefreshLastActivityTime();
            device?.CloseCallSession();
            await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.Ok, null);
        }

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
            SDPMediaAnnouncement? audio = sdp.Media
                .FirstOrDefault(media => media.Media == SDPMediaTypesEnum.audio);

            return audio?.MediaFormats.Values.Any(format =>
                string.Equals(format.Name(), nameof(SDPWellKnownMediaFormatsEnum.PCMU), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format.Name(), nameof(SDPWellKnownMediaFormatsEnum.PCMA), StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static TimeSpan GetRemainingTime(DateTimeOffset deadline)
        {
            TimeSpan remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Agent initialization timed out.");
            }

            return remaining;
        }

        private static Task<SocketError> SendResponseAsync(
            SIPTransport transport,
            SIPRequest request,
            SIPResponseStatusCodesEnum status,
            string? reason)
        {
            return transport.SendResponseAsync(SIPResponse.GetResponse(request, status, reason));
        }
    }
}
