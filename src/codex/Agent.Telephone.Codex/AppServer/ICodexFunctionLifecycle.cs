using Agent.Telephone.Codex.Abstractions.Common.Models;
using Agent.Telephone.Codex.Abstractions.Functions;

namespace Agent.Telephone.Codex.AppServer
{
    internal interface ICodexFunctionLifecycle : ICodexFunction
    {
        Task<CodexFunctionResult> ExecuteAsync(
            CodexFunctionRequest request,
            Func<CodexConversationId, CancellationToken, Task> onConversationCreated,
            CancellationToken cancellationToken);
    }
}
