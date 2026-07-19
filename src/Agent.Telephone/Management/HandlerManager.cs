using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers;
using Agent.Telephone.Handlers.SIPHandlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Management
{
    internal class HandlerManager : BaseManager
    {
        private readonly IDictionary<string, IHandler> _sipHandlers;

        public HandlerManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger<HandlerManager> logger)
            : base(serviceProvider, config, logger)
        {
            this._sipHandlers = new Dictionary<string, IHandler>();
        }
        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {

                #region SIP Handlers
                services.AddTransient<ActiveCallHandler>();
                services.AddTransient<RTPHandler>();
                #endregion

                services.AddSingleton<HandlerManager>();
            });
        }

        public override bool BuildComponent()
        {

            return true;
        }

        public override void OnSIPDeviceRegistered(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            ActiveCallHandler activeCallHandler = this.ServiceProvider.GetRequiredService<ActiveCallHandler>();
            RTPHandler rtpHandler = this.ServiceProvider.GetRequiredService<RTPHandler>();

            IDictionary<string, IHandler> handlerContainer = new Dictionary<string, IHandler>
            {
                [activeCallHandler.HandlerName] = activeCallHandler,
                [rtpHandler.HandlerName] = rtpHandler
            };
            bool buildResults = handlerContainer.Values
               .Select(h => h.Build(deviceContext))
               .All(result => result);

            if (!buildResults)
            {

            }

        }

    }
}