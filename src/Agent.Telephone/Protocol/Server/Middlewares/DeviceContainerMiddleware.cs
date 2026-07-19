using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Management;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Protocol.Server.Middlewares
{
    internal class DeviceContainerMiddleware
    {
        private SIPTransport? _sipTransport;

        private readonly DeviceManager _deviceManager;
        private readonly HandlerManager _handlerManager;
        private readonly ProviderManager _providerManager;
        private readonly ILogger<DeviceContainerMiddleware> _logger;
        public DeviceContainerMiddleware(DeviceManager deviceManager, HandlerManager handlerManager, ProviderManager providerManager, ILogger<DeviceContainerMiddleware> logger)
        {
            this._deviceManager = deviceManager;
            this._handlerManager = handlerManager;
            this._providerManager = providerManager;
            this._logger = logger;
        }
        public void SubscribeSIPTransportEvents(SIPTransport sipTransport)
        {
            this._sipTransport = sipTransport;
            this._sipTransport.SIPTransportRequestReceived += this.OnRequestReceivedAsync;
        }

        public void UnsubscribeSIPTransportEvents(SIPTransport sipTransport)
        {
            if (this._sipTransport is not null)
            {
                this._sipTransport.SIPTransportRequestReceived -= this.OnRequestReceivedAsync;
            }
        }

        private Task OnRequestReceivedAsync(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            if (this._sipTransport is null)
            {

                return Task.CompletedTask;
            }
            switch (sipRequest.Method)
            {
                case SIPMethodsEnum.REGISTER:
                    this.RegisterSIPDevice(this._sipTransport, sipRequest);
                    break;
                case SIPMethodsEnum.INVITE:
                    this.InviteSIPDevice(this._sipTransport, sipRequest);
                    break;
                case SIPMethodsEnum.BYE:
                    this.UnregisterSIPDevice(this._sipTransport, sipRequest);
                    break;
            }
            return Task.CompletedTask;
        }

        public bool RegisterSIPDevice(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            this._deviceManager.OnSIPDeviceRegistering(sipTransport, sipRequest);
            this._handlerManager.OnSIPDeviceRegistering(sipTransport, sipRequest);
            this._providerManager.OnSIPDeviceRegistering(sipTransport, sipRequest);




            return true;
        }

        public bool InviteSIPDevice(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            DeviceContext deviceContext = null!;
            this._handlerManager.OnSIPDeviceRegistered(deviceContext, sipTransport, sipRequest);
            this._providerManager.OnSIPDeviceRegistered(deviceContext, sipTransport, sipRequest);
            return true;
        }

        public bool UnregisterSIPDevice(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            return true;
        }
    }
}
