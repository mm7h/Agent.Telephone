using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Contexts;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers.CallControl
{
    /// <summary>
    /// Handler boundary for SIP signalling and RTP work. Providers only create
    /// validated commands and never construct SIP library objects directly.
    /// </summary>
    internal interface ICallTransferExecutor
    {
        Task<CallTransferResult> TransferAsync(
            CallTransferCommand command,
            CancellationToken cancellationToken);

        Task PlayFailureAsync(
            ActiveCallContext call,
            CallTransferResult result,
            CancellationToken cancellationToken);
    }

    internal sealed record CallTransferCommand(
        ActiveCallContext Call,
        RegisteredEndpoint Endpoint,
        IRegisteredEndpointLease Lease,
        AudioCodecsEnum Codec,
        int RingTimeoutSeconds);
}
