using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Store;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Management
{
    internal class DeviceManager : BaseManager
    {
        private readonly IStore _connectionStore;
        public DeviceManager(IStore store, IServiceProvider serviceProvider, TelephoneConfig config, ILogger<DeviceManager> logger) : base(serviceProvider, config, logger)
        {
            this._connectionStore = store;

        }
        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {

                services.AddSingleton<DeviceManager>();
            });
        }

        public override bool BuildComponent()
        {
            throw new NotImplementedException();
        }


        public override void OnSIPDeviceRegistering(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            /*
             string sessionId = $"{sipRequest.Header.CallId};from-tag={sipRequest.Header.From.FromTag};to-tag={sipRequest.Header.To.ToTag}";
            string callId = sipRequest.Header.CallId;
            string fromTag = sipRequest.Header.From.FromTag;
            string toTag = sipRequest.Header.To.ToTag;
             */

            string deviceId = sipRequest.Header.From.FromURI.User;
            DeviceContext? existingDeviceContext = this.GetDeviceContextById(deviceId);
            if (existingDeviceContext is null)
            {
                DeviceContext deviceContext = new DeviceContext(deviceId, sipTransport, sipRequest);
                this._connectionStore.Add(deviceId, deviceContext);
            }
        }

        public DeviceContext? GetDeviceContextById(string deviceId)
        {
            return this._connectionStore.Get<DeviceContext>(deviceId);
        }

        public override void OnSIPDeviceUnregister(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            base.OnSIPDeviceUnregister(deviceContext, sipTransport, sipRequest);
        }

    }
}
