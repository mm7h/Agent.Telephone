using Agent.Telephone.Abstractions.Configs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Management
{
    internal class ProviderManager : BaseManager
    {
        public ProviderManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger<ProviderManager> logger) : base(serviceProvider, config, logger)
        {
            
        }

        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {

                services.AddSingleton<ProviderManager>();
            });
        }

        public override bool BuildComponent()
        {
            throw new NotImplementedException();
        }



    }
}
