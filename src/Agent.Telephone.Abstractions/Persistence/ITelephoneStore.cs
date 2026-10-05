namespace Agent.Telephone.Abstractions.Persistence
{
    /// <summary>
    /// 为 Agent Telephone 数据提供持久化存储能力。
    /// </summary>
    public interface ITelephoneStore
    {
        /// <summary>
        /// 保存一段对话消息记录。
        /// 用户的一整句对话；
        /// 或者LLM一次生成的完整回复都应该作为一个 ConversationMessage 保存。
        /// </summary>
        Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存一个对话的句子。
        /// 用户的一整句对话；
        /// 或者LLM一次生成的完整回复被拆分为多个 MessageSegment 来保存。
        /// </summary>
        Task SaveMessageSegmentAsync(MessageSegment segment, CancellationToken cancellationToken = default);

        /// <summary>
        /// 更新助手的消息状态
        /// </summary>
        Task FinalizeAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string fullText,
            DeliveryState state,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 将助手消息标记为失败。
        /// </summary>
        Task FailAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string fullText,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 丢弃指定的助手消息。
        /// </summary>
        Task DiscardAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定用户和助手之间未读的助手消息。
        /// </summary>
        Task<IReadOnlyList<ConversationMessage>> GetUnreadAssistantMessagesAsync(string userAor, string assistantNumber, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定用户和助手之间的对话消息。
        /// </summary>
        Task<IReadOnlyList<ConversationMessage>> GetConversationMessagesAsync(string userAor, string assistantNumber, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定的助手消息。
        /// </summary>
        Task<ConversationMessage?> GetAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定消息的全部内容片段。
        /// </summary>
        Task<IReadOnlyList<MessageSegment>> GetMessageSegmentsAsync(string messageId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 将指定助手消息标记为已读。
        /// </summary>
        Task MarkAssistantMessageReadAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 将指定的多条助手消息标记为已读。
        /// </summary>
        Task MarkAssistantMessagesReadAsync(
            string userAor,
            string assistantNumber,
            IReadOnlyCollection<string> messageIds,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 清理过期的对话消息并返回清理结果。
        /// </summary>
        Task<MessageCleanupResult> CleanupConversationMessagesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存设备注册信息。
        /// </summary>
        Task SaveDeviceRegistrationAsync(DeviceRegistrationRecord registration, CancellationToken cancellationToken = default);

        /// <summary>
        /// 移除指定设备的注册信息。
        /// </summary>
        Task RemoveDeviceRegistrationAsync(string deviceId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取当前仍有效的设备注册信息。
        /// </summary>
        Task<IReadOnlyList<DeviceRegistrationRecord>> GetActiveDeviceRegistrationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    }
}
