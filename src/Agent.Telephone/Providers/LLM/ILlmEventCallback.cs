using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers.LLM
{
    internal interface ILlmEventCallback
    {
        Task OnBeforeFirstSegmentAsync(long turnId, OutSegment firstSegment, CancellationToken cancellationToken);
        Task OnSegmentAsync(long turnId, OutSegment segment, CancellationToken cancellationToken);
        Task OnToolExecutionPromptAsync(long turnId, OutSegment segment, CancellationToken cancellationToken) =>
            this.OnSegmentAsync(turnId, segment, cancellationToken);
        Task OnCompletedAsync(long turnId, CancellationToken cancellationToken);
        Task OnCancelledAsync(long turnId, CancellationToken cancellationToken);
        Task OnFailedAsync(long turnId, Exception exception, CancellationToken cancellationToken);
    }
}
