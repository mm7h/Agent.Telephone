using Agent.Telephone.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Agent.Telephone
{
    public static class EngineFactory
    {
        public static IServerBuilder CreateAgentTelephoneBuilder()
        {
            return ServerBuilder.CreateServerBuilder();
        }

        public static IServerBuilder AsAgentTelephoneHostBuilder(this IHostBuilder hostBuilder)
        {
            return ServerBuilder.CreateServerBuilder(hostBuilder);
        }
    }
}
