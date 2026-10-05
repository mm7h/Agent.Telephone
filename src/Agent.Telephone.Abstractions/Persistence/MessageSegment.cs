namespace Agent.Telephone.Abstractions.Persistence
{
    public sealed record MessageSegment
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string MessageId { get; init; } = string.Empty;
        public long Sequence { get; init; }
        public string TextContent { get; init; } = string.Empty;
        public string? AudioPath { get; init; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
