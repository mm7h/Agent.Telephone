using System.Net;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Resources;
using Agent.Telephone.Resources.Editors;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AudioEditorTests
{
    [Fact]
    public async Task PlaySIPCodeAudioAsync_EmitsCachedAudioInTwentyMillisecondFramesAsync()
    {
        byte[] audio = Enumerable.Range(0, 322).Select(index => (byte)index).ToArray();
        AudioEditor editor = CreateEditor(audio);
        List<(uint Duration, byte[] Data, bool IsFirst, bool IsLast)> frames = [];
        editor.OnAudioDataAvailable += (duration, data, isFirst, isLast) =>
            frames.Add((duration, data, isFirst, isLast));

        bool played = await editor.PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum.BusyHere,
            CancellationToken.None);

        Assert.True(played);
        Assert.Equal(2, frames.Count);
        Assert.Equal((uint)160, frames[0].Duration);
        Assert.Equal(audio[..320], frames[0].Data);
        Assert.True(frames[0].IsFirst);
        Assert.False(frames[0].IsLast);
        Assert.Equal((uint)1, frames[1].Duration);
        Assert.Equal(audio[320..], frames[1].Data);
        Assert.False(frames[1].IsFirst);
        Assert.True(frames[1].IsLast);
    }

    [Fact]
    public async Task PlaySIPCodeAudioLoopAsync_StopsWhenCancelledAsync()
    {
        AudioEditor editor = CreateEditor(new byte[320]);
        using CancellationTokenSource cancellation = new();
        int frames = 0;
        editor.OnAudioDataAvailable += (_, _, _, _) =>
        {
            frames++;
            cancellation.Cancel();
        };

        bool played = await editor.PlaySIPCodeAudioLoopAsync(
            SIPResponseStatusCodesEnum.BusyHere,
            cancellation.Token);

        Assert.False(played);
        Assert.Equal(1, frames);
    }

    [Fact]
    public async Task PlaySIPCodeAudioAsync_ReturnsFalseWhenAudioIsNotCachedAsync()
    {
        AudioEditor editor = CreateEditor(null);

        bool played = await editor.PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum.BusyHere,
            CancellationToken.None);

        Assert.False(played);
    }

    [Fact]
    public async Task PlaySIPCodeAudioAsync_DirectMediaSessionPlaybackCompletesAsync()
    {
        byte[] audio = new byte[320];
        AudioEditor editor = CreateEditor(audio);
        var encoder = new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat);
        var source = new AudioExtrasSource(
            encoder,
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        using var mediaSession = new VoIPMediaSession(
            new MediaEndPoints { AudioSource = source });
        AudioFormat pcmu = SupportedAudioFormats.SupportedSDPAudioFormat.First();
        mediaSession.SetDestination(
            SDPMediaTypesEnum.audio,
            new IPEndPoint(IPAddress.Loopback, 6000),
            new IPEndPoint(IPAddress.Loopback, 6001));

        bool played = await editor.PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum.BusyHere,
            mediaSession,
            pcmu,
            CancellationToken.None);

        Assert.True(played);
    }

    private static AudioEditor CreateEditor(byte[]? audio) => new(
        new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
        new StubAudioFileEncoder(),
        new StubAudioFileCaching(audio),
        NullLogger<AudioEditor>.Instance);

    private sealed class StubAudioFileEncoder : IAudioFileEncoder
    {
        public string ResourceName => nameof(StubAudioFileEncoder);

        public bool Load(ModelSetting settings) => true;

        public Task<bool> EncodeAudioFileAsync(
            string outputPath,
            float[] audioData,
            int sampleRate,
            int channels,
            int bitRate = 128000) => Task.FromResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class StubAudioFileCaching : IAudioFileCaching
    {
        private readonly byte[]? _audio;

        public StubAudioFileCaching(byte[]? audio)
        {
            this._audio = audio;
        }

        public string ResourceName => nameof(StubAudioFileCaching);

        public bool Load(IDictionary<int, string> settings) => true;

        public bool TryGetAudioBytes(
            SIPResponseStatusCodesEnum sipCode,
            out byte[]? audioBytes)
        {
            audioBytes = this._audio;
            return audioBytes is not null;
        }

        public void Dispose()
        {
        }
    }
}
