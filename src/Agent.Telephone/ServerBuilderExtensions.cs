using Microsoft.Extensions.Hosting;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Management;

namespace Agent.Telephone
{
    internal static class ServerBuilderExtensions
    {
        public static IHostBuilder RegisterLogger(this IHostBuilder builder, TelephoneConfig config)
        {
            return LoggerManager.RegisterServices(builder, config);
        }
        public static IHostBuilder RegisterResources(this IHostBuilder builder)
        {
            return ResourceManager.RegisterServices(builder);
        }

        public static IHostBuilder RegisterHandlers(this IHostBuilder builder)
        {
            return HandlerManager.RegisterServices(builder);
        }

        public static IHostBuilder RegisterProtocol(this IHostBuilder builder, TelephoneConfig config)
        {
            return ProtocolManager.RegisterServices(builder, config);
        }
    }
}
