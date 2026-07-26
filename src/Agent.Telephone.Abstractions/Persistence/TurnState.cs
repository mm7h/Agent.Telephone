namespace Agent.Telephone.Abstractions.Persistence
{
    public enum TurnState
    {
        Running,
        Completed,
        CancelledByUser,
        Interrupted,
        Failed
    }
}
