using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.LLM;

namespace Agent.Telephone.Providers
{
    internal interface ILlm : IProvider<LLMBuildConfig>
    {
        void RegisterDevice(ActiveCallContext activeCall, ILlmEventCallback callback);
        Task StartDialogueAsync(long turnId, string userMessage, CancellationToken token);
    }
}
