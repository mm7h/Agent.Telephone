namespace Agent.Telephone.Abstractions.Persistence
{
    public interface IMessageStore
    {
        Task SaveAsync(
            MessageRecord message,
            ReadOnlyMemory<byte> wave = default,
            CancellationToken cancellationToken = default);

        Task<MessageRecord?> GetAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<MessageRecord>> GetUnreadAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default);

        Task<bool> MarkReadAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default);

        Task<MessageCleanupResult> CleanupAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default);
    }
}
