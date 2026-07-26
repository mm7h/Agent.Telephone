namespace Agent.Telephone.Abstractions.Persistence
{
    public sealed record TurnRecord
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string UserAor { get; init; } = string.Empty;
        public string AssistantNumber { get; init; } = string.Empty;
        public string UserText { get; init; } = string.Empty;
        public string? ResponseText { get; init; }
        public TurnState State { get; init; } = TurnState.Running;
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; init; }
        public string? FailureReason { get; init; }
        public string? ResponseWavePath { get; init; }
    }
}
