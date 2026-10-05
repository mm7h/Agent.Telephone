using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Common.Configs
{
    internal record LLMBuildConfig(
        Dictionary<string, ModelSetting> AgentSettings,
        PrivateProvider SessionPrivateProvider,
        int? ResponseTimeoutSeconds = null);
}
