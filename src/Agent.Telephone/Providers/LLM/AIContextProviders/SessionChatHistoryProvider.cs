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
                return this._messages.Select(static message => message.Clone()).ToList();
            }
        }

        public void RemoveIncompleteFunctionCalls()
        {
            lock (this._lock)
            {
                HashSet<string> completedCallIds = this._messages
                    .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
                    .Select(static result => result.CallId)
                    .Where(static callId => !string.IsNullOrWhiteSpace(callId))
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<string> retainedCallIds = [];
                List<ChatMessage> validMessages = [];

                foreach (ChatMessage message in this._messages)
                {
                    FunctionCallContent[] calls = message.Contents.OfType<FunctionCallContent>().ToArray();
                    if (calls.Any(call => string.IsNullOrWhiteSpace(call.CallId) || !completedCallIds.Contains(call.CallId)))
                    {
                        continue;
                    }

                    foreach (FunctionCallContent call in calls)
                    {
                        retainedCallIds.Add(call.CallId);
                    }

                    validMessages.Add(message);
                }

                List<ChatMessage> filteredMessages = validMessages
                    .Where(message => message.Contents.OfType<FunctionResultContent>()
                        .All(result => !string.IsNullOrWhiteSpace(result.CallId) && retainedCallIds.Contains(result.CallId)))
                    .ToList();
                this._messages.Clear();
                this._messages.AddRange(filteredMessages);
            }
        }

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IEnumerable<ChatMessage>>(this.GetMessages());
        }

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            IEnumerable<ChatMessage> responseMessages = context.ResponseMessages ?? [];
            lock (this._lock)
            {
                foreach (ChatMessage message in context.RequestMessages.Concat(responseMessages))
                {
                    this._messages.Add(message.Clone());
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
