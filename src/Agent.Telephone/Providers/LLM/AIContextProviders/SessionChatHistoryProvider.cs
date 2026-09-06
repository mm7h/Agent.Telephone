using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.Providers.LLM.AIContextProviders
{
    /// <summary>
    /// 保存当前 Agent 会话中可供模型继续使用的聊天上下文。
    /// </summary>
    internal sealed class SessionChatHistoryProvider : ChatHistoryProvider
    {
        private readonly object _lock = new();
        private List<ChatMessage> _messages = [];

        public void Bind(List<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            lock (this._lock)
            {
                this._messages = messages;
            }
        }

        public void Append(ChatRole role, string? content)
        {
            if ((role != ChatRole.User && role != ChatRole.Assistant) || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            lock (this._lock)
            {
                this._messages.Add(new ChatMessage(role, content));
            }
        }

        public IReadOnlyList<ChatMessage> GetMessages()
        {
            lock (this._lock)
            {
                return this._messages.Select(static message => new ChatMessage(message.Role, message.Text)).ToList();
            }
        }

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IEnumerable<ChatMessage>>(this.GetMessages());
        }

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            IEnumerable<ChatMessage> responseMessages = context.ResponseMessages ?? [];
            foreach (ChatMessage message in context.RequestMessages.Concat(responseMessages))
            {
                if (this.IsConversationMessage(message))
                {
                    this.Append(message.Role, message.Text);
                }
            }

            return ValueTask.CompletedTask;
        }

        private bool IsConversationMessage(ChatMessage message)
        {
            return (message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                && !string.IsNullOrWhiteSpace(message.Text)
                && message.Contents.All(static content => content is not FunctionCallContent && content is not FunctionResultContent);
        }
    }
}
