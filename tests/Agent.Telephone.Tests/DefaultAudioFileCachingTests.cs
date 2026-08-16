using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Media.Abstractions.Common.Enums;
using Agent.Telephone.Resources.AudioFileCaching;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class DefaultAudioFileCachingTests
{
    [Fact]
    public void Load_CachesEmbeddedPromptMediaAndMapsSipCodes()
    {
        DefaultAudioFileCaching caching = new(
            static () => new TestStreamAudioPlayer(),
            NullLogger<DefaultAudioFileCaching>.Instance);

        Assert.True(caching.Load());
        foreach (int sipCode in new[] { 180, 402, 404, 480, 486, 488, 503 })
        {
            Assert.True(caching.TryGetAudioBytes((SIPResponseStatusCodesEnum)sipCode, out byte[]? sipAudio));
            Assert.True(sipAudio is { Length: > 0 });
        }

        Assert.True(caching.TryGetAudioBytes("numbers/1.mp3", out byte[]? numberAudio));
        Assert.True(numberAudio is { Length: > 0 });
        Assert.True(caching.TryGetAudioBytes("prompt/service_agent.mp3", out byte[]? promptAudio));
        Assert.True(promptAudio is { Length: > 0 });
    }

    private sealed class TestStreamAudioPlayer : IStreamAudioPlayer
    {
        public event Action<PlaybackState>? StateChanged
        {
            add { }
            remove { }
        }

        public event Action<TimeSpan>? PositionChanged
        {
            add { }
            remove { }
        }
        public event Action<float[], bool, bool>? OnAudioDataAvailable;

        public bool IsFFmpegInitialized => true;
        public bool IsLoaded => true;
        public TimeSpan Duration => TimeSpan.Zero;
        public TimeSpan Position => TimeSpan.Zero;
        public PlaybackState State => PlaybackState.Idle;
        public bool IsSeeking => false;
        public float Volume { get; set; }
        public ISampleProcessor? CustomSampleProcessor { get; set; }

        public Task<bool> CheckFFmpegInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> LoadAsync(Stream stream, int outputSampleRate, int outputChannels, int frameDuration, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            this.OnAudioDataAvailable?.Invoke([0.5f], true, true);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
