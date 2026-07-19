using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Protocol.Server;

namespace Agent.Telephone.Management
{
    internal class ProtocolManager : BaseManager
    {
        public ProtocolManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger<ProtocolManager> logger)
            : base(serviceProvider, config, logger)
        {
            
        }
        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            return builder.ConfigureServices((context, services) =>
            {
                services.RegisterSIPServerServices(config);

                services.AddSingleton<ProviderManager>();
            });
        }

        public override bool BuildComponent()
        {
            return true;
        }
    }
}
