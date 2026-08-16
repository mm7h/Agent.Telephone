using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.LLM;

namespace Agent.Telephone.Providers
{
    internal interface ILlm : IDisposable
    {
        bool Build(LLMBuildConfig settings);
        void RegisterDevice(ActiveCallContext activeCall, ILlmEventCallback callback);
        void UnregisterDevice(ActiveCallContext activeCall);
        Task StartDialogueAsync(string userMessage, CancellationToken token);
    }
}
