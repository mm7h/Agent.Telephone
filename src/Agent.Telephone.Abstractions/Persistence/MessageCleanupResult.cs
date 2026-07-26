namespace Agent.Telephone.Abstractions.Persistence
{
    public sealed record MessageCleanupResult(int ExpiredRemoved, int OverflowRemoved);
}
