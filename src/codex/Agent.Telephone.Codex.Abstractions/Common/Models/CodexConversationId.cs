namespace Agent.Telephone.Codex.Abstractions.Common.Models
{
    /// <summary>
    /// Codex 持久化 Thread 的标识。
    /// </summary>
    /// <param name="value">Thread 标识文本。</param>
    public sealed record CodexConversationId(string Value)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return this.Value;
        }
    }
}
