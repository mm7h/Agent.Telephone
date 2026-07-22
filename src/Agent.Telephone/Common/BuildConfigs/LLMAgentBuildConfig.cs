using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Common.Configs
{
    internal record LLMAgentBuildConfig(ModelSetting AgentSetting, PrivateProvider SessionPrivateProvider);
}
