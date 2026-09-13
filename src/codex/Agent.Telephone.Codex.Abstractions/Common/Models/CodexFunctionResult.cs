using Agent.Telephone.Codex.Abstractions.Common.Enums;

namespace Agent.Telephone.Codex.Abstractions.Common.Models
{
    /// <summary>
    /// 一次 Codex 指令的结果。
    /// </summary>
    /// <param name="status">执行状态。</param>
    /// <param name="output">最终 assistant 文本。</param>
    /// <param name="conversationId">本次执行对应的 Thread。</param>
    /// <param name="errorMessage">已脱敏的失败说明。</param>
    public sealed record CodexFunctionResult(
        CodexExecutionStatus Status,
        string Output,
        CodexConversationId? ConversationId,
        string? ErrorMessage = null);
}
