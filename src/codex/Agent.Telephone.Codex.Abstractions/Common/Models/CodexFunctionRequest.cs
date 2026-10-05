namespace Agent.Telephone.Codex.Abstractions.Common.Models
{
    /// <summary>
    /// 一次 Codex 指令。
    /// </summary>
    /// <param name="prompt">交给 Codex 的完整指令。</param>
    /// <param name="workingDirectory">Codex 的工作目录。</param>
    /// <param name="conversationId">要续接的 Thread；为空时创建新的 Thread。</param>
    public sealed record CodexFunctionRequest(
        string Prompt,
        string WorkingDirectory,
        CodexConversationId? ConversationId = null);
}
