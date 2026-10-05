using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.VAD;
using Agent.Telephone.Providers.VAD.Native;
using Agent.Telephone.Resources;
using Agent.Telephone.Resources.OnnxModels;
using Agent.Telephone.Resources.OnnxModels.VAD.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class SileroNativeVadTests
{
    [Fact]
    public async Task AnalysisVoiceAsync_ExcludesReplyWaitFromSilenceTimeoutAsync()
    {
        SequenceVadOnnxModel model = new([0, 0, 0]);
        VadCallback callback = new() { IsWaitingForReply = true };
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport);
        using ActiveCallContext call = this.CreateCall(transport, device);
        using SileroNative vad = this.CreateVad(model);
        vad.RegisterDevice(call, callback);
        Agent.Telephone.Providers.VAD.Contexts.VadSessionState state =
            (Agent.Telephone.Providers.VAD.Contexts.VadSessionState)typeof(SileroNative)
                .GetField("_vadSessionState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vad)!;
        state.HaveVoiceLatestTime = DateTimeOffset.Now.AddMinutes(-3).ToUnixTimeMilliseconds();
        float[] audio = new float[512];

        await vad.AnalysisVoiceAsync(call.DeviceId, audio, audio, CancellationToken.None);
        Assert.Equal(0, callback.LongTermSilenceCount);
        callback.IsWaitingForReply = false;
        await vad.AnalysisVoiceAsync(call.DeviceId, audio, audio, CancellationToken.None);
        Assert.Equal(0, callback.LongTermSilenceCount);

        state.HaveVoiceLatestTime = DateTimeOffset.Now.AddMinutes(-3).ToUnixTimeMilliseconds();
        await vad.AnalysisVoiceAsync(call.DeviceId, audio, audio, CancellationToken.None);
        Assert.Equal(1, callback.LongTermSilenceCount);
    }

    [Fact]
    public async Task AnalysisVoiceAsync_DoesNotSplitContinuousSpeech()
    {
        var model = new SequenceVadOnnxModel(Enumerable.Repeat(0.9f, 30));
        var callback = new VadCallback();
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport);
        using ActiveCallContext call = this.CreateCall(transport, device);
        using var vad = this.CreateVad(model);
        vad.RegisterDevice(call, callback);

        for (int index = 0; index < 30; index++)
        {
            float[] audio = new float[512];
            await vad.AnalysisVoiceAsync(call.DeviceId, audio, audio, CancellationToken.None);
        }

        Assert.Equal(30, model.InferCallCount);
        Assert.Empty(callback.DetectedSegments);
    }

    [Fact]
    public async Task AnalysisVoiceAsync_EmitsOnlyAfterConfiguredContinuousSilence()
    {
        var probabilities = Enumerable.Repeat(0.9f, 5)
            .Concat(Enumerable.Repeat(0.0f, 22));
        var model = new SequenceVadOnnxModel(probabilities);
        var callback = new VadCallback();
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport);
        using ActiveCallContext call = this.CreateCall(transport, device);
        using var vad = this.CreateVad(model);
        vad.RegisterDevice(call, callback);
        float[] segmentAudio = new float[27 * 512];

        for (int index = 0; index < 26; index++)
        {
            await vad.AnalysisVoiceAsync(call.DeviceId, new float[512], segmentAudio, CancellationToken.None);
        }

        Assert.Empty(callback.DetectedSegments);

        await vad.AnalysisVoiceAsync(call.DeviceId, new float[512], segmentAudio, CancellationToken.None);

        Assert.Collection(callback.DetectedSegments, detected => Assert.Equal(5 * 512, detected.Length));
    }

    [Fact]
    public async Task AnalysisVoiceAsync_RestartsSilenceTimerWhenSpeechResumes()
    {
        var probabilities = Enumerable.Repeat(0.9f, 5)
            .Concat(Enumerable.Repeat(0.0f, 10))
            .Append(0.9f)
            .Concat(Enumerable.Repeat(0.0f, 22));
        var model = new SequenceVadOnnxModel(probabilities);
        var callback = new VadCallback();
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport);
        using ActiveCallContext call = this.CreateCall(transport, device);
        using var vad = this.CreateVad(model);
        vad.RegisterDevice(call, callback);
        float[] segmentAudio = new float[38 * 512];

        for (int index = 0; index < 37; index++)
        {
            await vad.AnalysisVoiceAsync(call.DeviceId, new float[512], segmentAudio, CancellationToken.None);
        }

        Assert.Empty(callback.DetectedSegments);

        await vad.AnalysisVoiceAsync(call.DeviceId, new float[512], segmentAudio, CancellationToken.None);

        Assert.Collection(callback.DetectedSegments, detected => Assert.Equal(6 * 512, detected.Length));
    }

    [Fact]
    public async Task AnalysisVoiceAsync_CombinesPartialInputFramesWithoutReprocessingAudio()
    {
        var model = new SequenceVadOnnxModel([0.9f]);
        var callback = new VadCallback();
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport);
        using ActiveCallContext call = this.CreateCall(transport, device);
        using var vad = this.CreateVad(model);
        vad.RegisterDevice(call, callback);

        await vad.AnalysisVoiceAsync(call.DeviceId, new float[320], new float[320], CancellationToken.None);
        await vad.AnalysisVoiceAsync(call.DeviceId, new float[192], new float[512], CancellationToken.None);

        Assert.Equal(1, model.InferCallCount);
        Assert.Empty(callback.DetectedSegments);
    }

    private SileroNative CreateVad(IVadOnnxModel model)
    {
        var vad = new SileroNative(new VadServiceProvider(model), NullLogger<SileroNative>.Instance);
        bool built = vad.Build(new ModelSetting
        {
            ModelName = nameof(SileroNative),
            Config = new Dictionary<string, string>
            {
                ["SilenceThresholdSecond"] = "0.7",
                ["Threshold"] = "0.5",
                ["ThresholdLow"] = "0.2"
            }
        });

        Assert.True(built);
        return vad;
    }

    private DeviceContext CreateDevice(SIPTransport transport)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var registration = new DeviceRegistrationRecord(
            "device-1",
            "sip:device-1@device.test",
            "sip:device-1@192.0.2.10:5060",
            now,
            now,
            now.AddMinutes(5));
        return new DeviceContext(
            TestServices.ScopeFactory,
            transport,
            registration,
            [new AssistantConfig { DialingNumber = "10000" }]);
    }

    private ActiveCallContext CreateCall(SIPTransport transport, DeviceContext device)
    {
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        return new ActiveCallContext(
            device,
            "sip:1001@device.test",
            "10000",
            userAgent,
            mediaSession);
    }

    private sealed class VadServiceProvider : IServiceProvider
    {
        private readonly IVadOnnxModel _model;

        public VadServiceProvider(IVadOnnxModel model)
        {
            this._model = model;
        }

        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IVadOnnxModel) ? this._model : null;
        }
    }

    private sealed class SequenceVadOnnxModel : IVadOnnxModel
    {
        private readonly Queue<float> _probabilities;

        public SequenceVadOnnxModel(IEnumerable<float> probabilities)
        {
            this._probabilities = new Queue<float>(probabilities);
        }

        public string ResourceName => "test";
        public string ModelType => "vad";
        public string ModelName => "test";
        public int InferCallCount { get; private set; }

        public bool Load(ModelSetting settings) => true;

        public float Infer(float[] audioSamples, int sampleRate, SileroModelState modelState)
        {
            Assert.Equal(512, audioSamples.Length);
            Assert.Equal(16000, sampleRate);
            this.InferCallCount++;
            return this._probabilities.Dequeue();
        }

        public void Dispose()
        {
        }
    }

    private sealed class VadCallback : IVadEventCallback
    {
        public List<float[]> DetectedSegments { get; } = [];

        public void OnVoiceStarted()
        {
        }

        public void OnVoiceDetected(float[] audioData)
        {
            this.DetectedSegments.Add(audioData);
        }

        public void OnVoiceSilence()
        {
        }

        public bool IsWaitingForReply { get; set; }
        public int LongTermSilenceCount { get; private set; }

        public void OnLongTermSilence()
        {
            this.LongTermSilenceCount++;
        }
    }
}
