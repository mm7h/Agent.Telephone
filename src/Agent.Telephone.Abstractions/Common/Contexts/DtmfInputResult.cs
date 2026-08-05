namespace Agent.Telephone.Abstractions.Common.Contexts
{
    public enum DtmfInputStatus
    {
        Accepted,
        InvalidKey,
        AlreadyWaiting,
        CallEnded,
        Unavailable,
    }

    /// <summary>
    /// Result of asking the active call to accept a DTMF menu selection.
    /// </summary>
    public sealed record DtmfInputResult(DtmfInputStatus Status, string? Message = null)
    {
        public bool Succeeded => this.Status == DtmfInputStatus.Accepted;
    }
}
