using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.Providers
{
    //todo
    internal interface ILlm //: IProvider<LLMBuildConfig>
    {
        event Action OnBeforeTokenGenerate;
        event Action<OutSegment> OnTokenGenerating;
        event Action<IEnumerable<OutSegment>> OnTokenGenerated;

        IReadOnlyList<ChatMessage> GetChatHistory();
        Task StartDialogueAsync(string userMessage, CancellationToken token);
    }
}
