using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;

namespace Agent.Telephone.FunctionTools.Adapters
{
    internal sealed class ServerInfoAdapter : IServerInfo
    {
        public ServerInfoAdapter(string serverName, TelephoneConfig config)
        {
            this.ServerName = serverName;
            this.Config = config;
        }

        public string ServerName { get; }

        public TelephoneConfig Config { get; }

    }
}
