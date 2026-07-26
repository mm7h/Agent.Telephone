namespace Agent.Telephone.Abstractions.Persistence
{
    public interface ITurnStore
    {
        Task SaveAsync(
            TurnRecord turn,
            ReadOnlyMemory<byte> responseWave = default,
            CancellationToken cancellationToken = default);

        Task<TurnRecord?> GetAsync(
            string userAor,
            string assistantNumber,
            string turnId,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<TurnRecord>> GetByStateAsync(
            TurnState state,
            CancellationToken cancellationToken = default);
    }
}
