using Microsoft.Extensions.Hosting;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.ObjectPoolPolicies;
using Agent.Telephone.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone
{
    internal static class ServerBuilderExtensions
    {
        public static IHostBuilder RegisterLogger(this IHostBuilder builder, TelephoneConfig config)
        {
            return LoggerManager.RegisterServices(builder, config);
        }
        public static IHostBuilder RegisterResources(this IHostBuilder builder, TelephoneConfig config)
        {
            return ResourceManager.RegisterServices(builder,config);
        }

        public static IHostBuilder RegisterHandlers(this IHostBuilder builder)
        {
            return HandlerManager.RegisterServices(builder);
        }
        public static IHostBuilder RegisterObjectPools(this IHostBuilder builder)
        {
            return builder.ConfigureServices((_, services) =>
            {
                services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
                services.AddSingleton<ObjectPool<OutSegment>>(serviceProvider =>
                    serviceProvider.GetRequiredService<ObjectPoolProvider>().Create(new OutSegmentPolicy()));
                services.AddSingleton<ObjectPool<Workflow<float[]>>>(serviceProvider =>
                    serviceProvider.GetRequiredService<ObjectPoolProvider>().Create(new WorkflowPolicy<float[]>()));
                services.AddSingleton<ObjectPool<Workflow<string>>>(serviceProvider =>
                    serviceProvider.GetRequiredService<ObjectPoolProvider>().Create(new WorkflowPolicy<string>()));
                services.AddSingleton<ObjectPool<Workflow<OutSegment>>>(serviceProvider =>
                    serviceProvider.GetRequiredService<ObjectPoolProvider>().Create(new WorkflowPolicy<OutSegment>()));
            });
        }
        public static IHostBuilder RegisterProviders(this IHostBuilder builder, TelephoneConfig config)
        {
            return ProviderManager.RegisterServices(builder, config);
        }
        public static IHostBuilder RegisterDevices(this IHostBuilder builder)
        {
            return DeviceContextManager.RegisterServices(builder);
        }

        public static IHostBuilder RegisterProtocol(this IHostBuilder builder, TelephoneConfig config)
        {
            return ProtocolManager.RegisterServices(builder, config);
        }
    }
}
