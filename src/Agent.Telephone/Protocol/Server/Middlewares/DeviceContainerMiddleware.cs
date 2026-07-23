using Agent.Telephone.Abstractions;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Management;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Protocol.Server.Middlewares
{
    internal sealed class DeviceContainerMiddleware
    {
        private SIPTransport? _sipTransport;

        private readonly IBasicVerify _basicVerify;
        private readonly DeviceContextManager _deviceManager;
        private readonly HandlerManager _handlerManager;
        private readonly ProviderManager _providerManager;
        private readonly ILogger<DeviceContainerMiddleware> _logger;

        public DeviceContainerMiddleware(IBasicVerify basicVerify,
            DeviceContextManager deviceManager, 
            HandlerManager handlerManager, 
            ProviderManager providerManager, 
            ILogger<DeviceContainerMiddleware> logger)
        {
            this._basicVerify = basicVerify;
            this._deviceManager = deviceManager;
            this._handlerManager = handlerManager;
            this._providerManager = providerManager;
            this._logger = logger;
        }

        public void SubscribeSIPTransportEvents(SIPTransport sipTransport)
        {
            this._sipTransport = sipTransport;
            sipTransport.SIPTransportRequestReceived += this.OnRequestReceivedAsync;
        }

        public void UnsubscribeSIPTransportEvents(SIPTransport sipTransport) => sipTransport.SIPTransportRequestReceived -= this.OnRequestReceivedAsync;

        private async Task OnRequestReceivedAsync(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
        {
            if (this._sipTransport is null)
            {
                return;
            }
            switch (request.Method)
            {
                case SIPMethodsEnum.REGISTER:
                    await this.RegisterSIPDeviceAsync(this._sipTransport, request);
                    break;
                case SIPMethodsEnum.INVITE:
                    await this.InviteSIPDeviceAsync(this._sipTransport, request);
                    break;
                case SIPMethodsEnum.BYE:
                    this.UnregisterSIPDevice(request);
                    break;
            }
        }

        private async Task RegisterSIPDeviceAsync(SIPTransport transport, SIPRequest request)
        {
            await this._deviceManager.OnSIPDeviceRegisteringAsync(transport, request);
            var response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
            response.Header.Contact = request.Header.Contact;
            await transport.SendResponseAsync(response);
        }

        private async Task InviteSIPDeviceAsync(SIPTransport transport, SIPRequest request)
        {
            DeviceContext? device = this._deviceManager.GetSIPDeviceById(request);
            if (device is null)
            {
                await transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Forbidden, "Device is not registered"));
                return;
            }

            device.InitializeCallSession(request);

            if (!await this._providerManager.OnSIPDeviceRegisteredAsync(device, transport, request) || !await this._handlerManager.OnSIPDeviceRegisteredAsync(device, transport, request))
            {
                device.CloseCallSession();
                await transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.NotAcceptableHere, "Audio pipeline unavailable"));
            }
        }

        private void UnregisterSIPDevice(SIPRequest request)
        {
            this._deviceManager.GetSIPDeviceById(request)?.CloseCallSession();
        }
    }
}
