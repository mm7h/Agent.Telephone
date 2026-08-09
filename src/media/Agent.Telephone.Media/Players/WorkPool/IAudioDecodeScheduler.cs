using Agent.Telephone.Media.Players.Contexts;
using Agent.Telephone.Media.Abstractions;

namespace Agent.Telephone.Media.Players.WorkPool;

/// <summary>
/// 提供固定专用线程的短批次音频解码调度器。
/// </summary>
internal interface IAudioDecodeScheduler : IDisposable
{
    AudioPlayerOptions Options { get; }

    Task RunAsync(Action<CancellationToken> action, CancellationToken cancellationToken = default);

    Task<TResult> RunAsync<TResult>(Func<CancellationToken, TResult> function, CancellationToken cancellationToken = default);

    Task RunCleanupAsync(Action<CancellationToken> action);

    bool TryAcquirePlaybackContext();

    void ReleasePlaybackContext();

    bool TryReserveBuffer(long bytes, AudioPlaybackContext context);

    void ReleaseBuffer(long bytes);

    void Schedule(AudioPlaybackContext context, AudioDecodeWorkPriority priority);

    void Unregister(AudioPlaybackContext context);
}
