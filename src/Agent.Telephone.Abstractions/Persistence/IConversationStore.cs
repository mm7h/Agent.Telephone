namespace Agent.Telephone.Abstractions.Persistence
{
    public interface IConversationStore
    {
        Task SaveAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ConversationTurn>> GetRecentAsync(
            string userAor,
            string assistantNumber,
            int? maximumTurns = null,
            CancellationToken cancellationToken = default);
    }
}
