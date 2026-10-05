using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Protocol.Server.Middlewares;
using Microsoft.Extensions.DependencyInjection;
using SIPSorcery.SIP;

namespace Agent.Telephone.Protocol.Server
{
    internal static class SIPServerBuilderExtensions
    {
        /// <summary>
        /// 注册SIP服务器相关的服务
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="config"></param>
        /// <returns></returns>
        public static IServiceCollection RegisterSIPServerServices(this IServiceCollection services, TelephoneConfig config)
        {
            return services.AddSingleton(config.SIPConfig)
                .AddSingleton<DeviceContainerMiddleware>()
                .AddSingleton<SIPTransport>()
                .AddHostedService<SipServerHostedService>();
        }
    }
}
