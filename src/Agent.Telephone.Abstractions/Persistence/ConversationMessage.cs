namespace Agent.Telephone.Abstractions.Persistence
{
    public sealed record ConversationMessage
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string TurnId { get; init; } = string.Empty;
        public string UserAor { get; init; } = string.Empty;
        public string AssistantNumber { get; init; } = string.Empty;
        public ConversationRole Role { get; init; }
        public string FullText { get; init; } = string.Empty;
        public DeliveryState State { get; init; } = DeliveryState.Read;
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ReadAt { get; init; }
    }
}
