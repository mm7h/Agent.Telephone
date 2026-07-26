namespace Agent.Telephone.Abstractions.Persistence
{
    public sealed record ConversationTurn
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string UserAor { get; init; } = string.Empty;
        public string AssistantNumber { get; init; } = string.Empty;
        public string UserText { get; init; } = string.Empty;
        public string AssistantText { get; init; } = string.Empty;
        public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
