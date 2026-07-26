namespace Agent.Telephone.Abstractions.FunctionTools
{
    /// <summary>
    /// Exposes call control to private function tools without leaking SIP library types.
    /// </summary>
    public interface ICallControl
    {
        string? CallerNumber { get; }
        string? AssistantNumber { get; }
        bool IsCallActive { get; }

        Task<CallTransferResult> TransferAsync(string targetNumber, CancellationToken cancellationToken = default);
    }
}
