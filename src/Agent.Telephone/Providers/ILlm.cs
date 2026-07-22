using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.Providers
{
    internal interface ILlm : IDisposable
    {
        event Action OnBeforeTokenGenerate;
        event Action<OutSegment> OnTokenGenerating;
        event Action<IEnumerable<OutSegment>> OnTokenGenerated;

        IReadOnlyList<ChatMessage> GetChatHistory();
        bool Build(LLMBuildConfig settings);
        void RegisterDevice(string deviceId);
        void UnregisterDevice(string deviceId);
        Task StartDialogueAsync(string userMessage, CancellationToken token);
    }
}
