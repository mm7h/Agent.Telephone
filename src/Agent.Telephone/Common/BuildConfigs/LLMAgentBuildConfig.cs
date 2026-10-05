using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

using Agent.Telephone.Providers.LLM.AIContextProviders;

namespace Agent.Telephone.Common.Configs
{
    internal record LLMAgentBuildConfig(ModelSetting AgentSetting, PrivateProvider SessionPrivateProvider, SessionChatHistoryProvider ChatHistoryProvider);
}
