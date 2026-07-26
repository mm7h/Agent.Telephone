namespace Agent.Telephone.Abstractions.FunctionTools
{
    public enum CallTransferStatus
    {
        Success = 0,
        InvalidTarget,
        SelfTransfer,
        Offline,
        Busy,
        TimedOut,
        Rejected,
        CodecNotSupported,
        CallEnded,
        Failed,
    }

    public sealed record CallTransferResult(
        CallTransferStatus Status,
        string TargetNumber,
        string? Message = null)
    {
        public bool Succeeded => this.Status == CallTransferStatus.Success;
    }
}
