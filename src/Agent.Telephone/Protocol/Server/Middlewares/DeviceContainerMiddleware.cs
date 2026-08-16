using System.Net.Sockets;
using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;

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
        private readonly ILogger<DeviceContainerMiddleware> _logger;

        public DeviceContainerMiddleware(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            DeviceContextManager deviceManager,
            FunctionToolManager functionToolManager,
            HandlerManager handlerManager,
            ProviderManager providerManager,
            ILogger<DeviceContainerMiddleware> logger)
        {
            this._serviceProvider = serviceProvider;
            this._config = config;
            this._deviceManager = deviceManager;
            this._functionToolManager = functionToolManager;
            this._handlerManager = handlerManager;
            this._providerManager = providerManager;
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
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.InternalServerError, "SIP request processing failed");
            }
        }

        #region Verify
        private async Task<bool> VerifySIPDeviceAsync(SIPTransport transport, SIPEndPoint remote, SIPRequest request)
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
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.Forbidden, "Authentication unavailable");
                return false;
            }

            try
            {
                var remoteEndPoint = remote.GetIPEndPoint();
                if (verifier.Verify(callerAor.User, remoteEndPoint))
                {
                    return true;
                }

                this._logger.LogWarning("设备 {dialingNumber} 未通过 SIP 认证，来源 {remoteEndPoint}。", callerAor.User, remoteEndPoint);
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "验证设备 {dialingNumber} 的 SIP 请求时发生异常。", callerAor.User);
            }

            await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.Forbidden, "Authentication failed");
            return false;
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
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.Forbidden, "Device is not registered");
                return;
            }

            device.RefreshLastActivityTime();
            string assistantNumber = request.GetAssistantNumber();
            if (!device.AvailableAssistants.ContainsKey(assistantNumber))
            {
                this._logger.LogError("设备 {deviceId} 的 INVITE 请求中指定的助手号码 {assistantNumber} 不存在。", device.DeviceId, assistantNumber);
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.NotFound, "Assistant number not found");
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
                this._logger.LogError("设备 {deviceId} 的 INVITE 不包含受支持的音频格式。", device.DeviceId);
                await SendResponseAsync(transport, request, SIPResponseStatusCodesEnum.NotAcceptableHere, "PCMU or PCMA audio is required");
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
                    device.RejectPendingCall(activeCall, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "FunctionTool initialization failed");
                    return;
                }

                bool providersBuilt = await this._providerManager
                    .OnSIPDeviceRegisteredAsync(device, transport, request)
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

                if (!providersBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    this._logger.LogError("设备 {deviceId} 的 Providers 初始化失败。", device.DeviceId);
                    device.RejectPendingCall(activeCall, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Agent provider initialization failed");
                    return;
                }

                bool handlersBuilt = await this._handlerManager.OnSIPDeviceRegisteredAsync(device, transport, request).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

                if (!handlersBuilt || !ReferenceEquals(device.ActiveCall, activeCall))
                {
                    this._logger.LogError("设备 {deviceId} 的 Handlers 初始化失败。", device.DeviceId);
                    device.RejectPendingCall(activeCall, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Audio pipeline unavailable");
                    return;
                }


                this._logger.LogInformation("已接听来自号码：{callerNumber} 的呼叫，拨号号码：{dialedNumber}。", activeCall.CallerNumber, activeCall.DialedNumber);
                return;

            }
            catch (TimeoutException)
            {
                this._logger.LogWarning("设备 {deviceId} 的 Agent 初始化超过 {timeoutSeconds} 秒。", device.DeviceId, this._config.SIPConfig.AgentInitializationTimeoutSeconds);
                device.RejectPendingCall(activeCall, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Agent initialization timed out");
            }
            catch (Exception exception)
            {
                device.RejectPendingCall(activeCall, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Agent initialization failed");
                this._logger.LogError(exception, "设备 {deviceId} 的 Agent 初始化失败。", device.DeviceId);
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

        private static Task<SocketError> SendResponseAsync(SIPTransport transport, SIPRequest request, SIPResponseStatusCodesEnum status, string? reason)
        {
            return transport.SendResponseAsync(SIPResponse.GetResponse(request, status, reason));
        } 
        #endregion
    }
}
