namespace Agent.Telephone.Abstractions.Persistence
{
    public sealed record MessageRecord
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string TurnId { get; init; } = string.Empty;
        public string UserAor { get; init; } = string.Empty;
        public string AssistantNumber { get; init; } = string.Empty;
        public string AudioPath { get; init; } = string.Empty;
        public long Sequence { get; init; }
        public DeliveryState State { get; init; } = DeliveryState.Unread;
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ReadAt { get; init; }
    }
}
