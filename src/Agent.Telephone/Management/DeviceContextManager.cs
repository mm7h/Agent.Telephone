using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Store;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Management
{
    internal class DeviceContextManager : BaseManager
    {
        private readonly IStore _connectionStore;
        public DeviceContextManager(IStore store, IServiceProvider serviceProvider, TelephoneConfig config, ILogger<DeviceContextManager> logger) : base(serviceProvider, config, logger)
        {
            this._connectionStore = store;

        }
        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {

                services.AddSingleton<DeviceContextManager>();
            });
        }

        public override bool BuildComponent()
        {
            return true;
        }


        public override Task OnSIPDeviceRegisteringAsync(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            string deviceId = sipRequest.GetDeviceId();
            DeviceContext? existingDeviceContext = this.GetSIPDeviceById(sipRequest);
            if (existingDeviceContext is null)
            {
                this.RegisterSIPDevice(sipTransport, sipRequest);
            }
            return Task.CompletedTask;
        }

        public DeviceContext RegisterSIPDevice(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            DeviceContext deviceContext = new DeviceContext(sipTransport, sipRequest, this.Config.AssistantConfigs);
            this._connectionStore.Add(deviceContext.DeviceId, deviceContext);
            return deviceContext;
        }

        public DeviceContext? GetSIPDeviceById(SIPRequest sipRequest)
        {
            string deviceId = sipRequest.GetDeviceId();
            return this._connectionStore.Contains(deviceId) ? this._connectionStore.Get<DeviceContext>(deviceId) : null;
        }

        public void RemoveSIPDevice(SIPRequest sipRequest)
        {
            DeviceContext? device = this.GetSIPDeviceById(sipRequest);
            device?.Dispose();
            this._connectionStore.Remove(sipRequest.GetDeviceId());
        }

        public override Task OnSIPDeviceUnregisterAsync(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            return base.OnSIPDeviceUnregisterAsync(deviceContext, sipTransport, sipRequest);
        }
    }
}
