using Agent.Telephone.Common.Configs;
using Microsoft.Agents.AI.Workflows;

namespace Agent.Telephone.Providers.LLM
{
    internal interface IAgent : IIdentified, IDisposable
    {
        string AgentName { get; }
        string Prompt { get; }
        int Order { get; }
        bool IsEnabled { get; }
        bool SupportsStreaming { get; }
        bool Build(LLMAgentBuildConfig settings);
        Executor AsExecutor();
        void RegisterDevice(string deviceId);
        void UnregisterDevice(string deviceId);
    }
}
