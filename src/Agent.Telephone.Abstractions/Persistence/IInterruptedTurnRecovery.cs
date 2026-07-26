namespace Agent.Telephone.Abstractions.Persistence
{
    public interface IInterruptedTurnRecovery
    {
        Task<int> RecoverAsync(
            string interruptionText,
            ReadOnlyMemory<byte> interruptionWave = default,
            CancellationToken cancellationToken = default);
    }
}
