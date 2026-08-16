using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers.LLM
{
    internal interface ILlmEventCallback
    {
        Task OnBeforeFirstSegmentAsync(OutSegment firstSegment, CancellationToken cancellationToken);
        Task OnSegmentAsync(OutSegment segment, CancellationToken cancellationToken);
        Task OnCompletedAsync(CancellationToken cancellationToken);
        Task OnCancelledAsync(CancellationToken cancellationToken);
        Task OnFailedAsync(Exception exception, CancellationToken cancellationToken);
    }
}
