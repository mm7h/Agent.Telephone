using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class PrivateProviderLifetimeTests
{
    [Fact]
    public void Dispose_UnregistersSessionProvidersWithoutDisposingScopeOwnedInstances()
    {
        using TestCallSession session = CreateCall();
        var audioProcessor = new TrackingProvider("audio-processor", isSherpaModel: false);
        var vad = new TrackingProvider("vad", isSherpaModel: false);
        var asr = new TrackingProvider("asr", isSherpaModel: false);
        var llm = new TrackingLlm();
        var tts = new TrackingProvider("tts", isSherpaModel: false);
        var offlineDialogue = new TrackingProvider("offline-dialogue", isSherpaModel: false);
        var callControl = new TrackingProvider("call-control", isSherpaModel: false);
        var dtmfInput = new TrackingProvider("dtmf-input", isSherpaModel: false);

        PrivateProvider providers = session.Call.AIAgentContext.PrivateProvider;
        providers.SetAudioProcessor(audioProcessor);
        providers.SetVad(vad);
        providers.SetAsr(asr);
        providers.SetLlm(llm);
        providers.SetTts(tts);
        providers.SetOfflineDialogue(offlineDialogue);
        providers.SetCallControl(callControl);
        providers.SetDtmfInput(dtmfInput);

        providers.Dispose();
        providers.Dispose();

        AssertProviderReleased(audioProcessor);
        AssertProviderReleased(vad);
        AssertProviderReleased(asr);
        AssertProviderReleased(tts);
        AssertProviderReleased(offlineDialogue);
        AssertProviderReleased(callControl);
        AssertProviderReleased(dtmfInput);
        Assert.Equal(["unregister"], llm.Events);
    }

    [Fact]
    public void Dispose_UnregistersButDoesNotDisposeSherpaProvider()
    {
        using TestCallSession session = CreateCall();
        var sherpaTts = new TrackingProvider("tts", isSherpaModel: true);
        PrivateProvider providers = session.Call.AIAgentContext.PrivateProvider;
        providers.SetTts(sherpaTts);

        providers.Dispose();

        Assert.Equal(["unregister"], sherpaTts.Events);
    }

    [Fact]
    public async Task DisposeAsync_ContinuesUnregisteringAfterOneProviderFailsAsync()
    {
        using TestCallSession session = CreateCall();
        TrackingProvider audioProcessor = new("audio-processor", false) { FailUnregister = true };
        TrackingProvider vad = new("vad", true);
        PrivateProvider providers = session.Call.AIAgentContext.PrivateProvider;
        providers.SetAudioProcessor(audioProcessor);
        providers.SetVad(vad);

        await Assert.ThrowsAsync<AggregateException>(() => providers.DisposeAsync());
        AssertProviderReleased(audioProcessor);
        AssertProviderReleased(vad);
        await providers.DisposeAsync();
        AssertProviderReleased(vad);
    }

    private static void AssertProviderReleased(TrackingProvider provider)
    {
        Assert.Equal(["unregister"], provider.Events);
    }

    private static TestCallSession CreateCall()
    {
        SIPTransport transport = new();
        DeviceContext device = new(
            TestServices.ScopeFactory,
            transport,
            CreateRegisterRequest(),
            SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060"),
            300,
            [new AssistantConfig { DialingNumber = "10000" }]);
        Assert.True(device.TryBeginCallback());
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        Assert.True(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            "10000",
            userAgent,
            mediaSession,
            out ActiveCallContext? call));
        Assert.NotNull(call);
        device.EndCallback();
        return new TestCallSession(transport, device, call);
    }

    private static SIPRequest CreateRegisterRequest()
    {
        SIPRequest request = SIPRequest.GetRequest(
            SIPMethodsEnum.REGISTER,
            SIPURI.ParseSIPURI("sip:registrar@127.0.0.1"));
        request.Header.From = new SIPFromHeader(
            null,
            SIPURI.ParseSIPURI("sip:1001@device.test"),
            CallProperties.CreateNewTag());
        request.Header.To = new SIPToHeader(
            null,
            SIPURI.ParseSIPURI("sip:registrar@server.test"),
            null);
        request.Header.Contact = [new SIPContactHeader(
            null,
            SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060")) { Expires = 300 }];
        request.Header.Expires = 300;
        return request;
    }

    private sealed class TrackingProvider : IAudioProcessor, IVad, IAsr, ITts, IOfflineDialogue, ICallControl, IDtmfInput
    {
        public TrackingProvider(string providerType, bool isSherpaModel)
        {
            this.ProviderType = providerType;
            this.IsSherpaModel = isSherpaModel;
        }

        public bool FailUnregister { get; init; }
        public List<string> Events { get; } = [];
        public string ProviderType { get; }
        public string ModelName => nameof(TrackingProvider);
        public bool IsSherpaModel { get; }
        public bool IsStreaming => false;
        public int FrameSize => 0;
        public event Action<float[], bool, bool, string?>? OnMixedAudioDataAvailable
        {
            add { }
            remove { }
        }
        public bool Build(ModelSetting settings) => true;
        public bool Build(List<AssistantConfig> settings) => true;
        public void RegisterDevice(ActiveCallContext activeCall) { }
        public void RegisterDevice(ActiveCallContext activeCall, Agent.Telephone.Providers.VAD.IVadEventCallback callback) { }
        public void RegisterDevice(ActiveCallContext activeCall, Agent.Telephone.Providers.ASR.IAsrEventCallback callback) { }
        public void RegisterDevice(ActiveCallContext activeCall, Agent.Telephone.Providers.TTS.ITtsEventCallback callback) { }
        public void UnregisterDevice(ActiveCallContext activeCall)
        {
            this.Events.Add("unregister");
            if (this.FailUnregister)
            {
                throw new InvalidOperationException("Unregister failed.");
            }
        }
        public void Dispose() => this.Events.Add("dispose");
        public Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token) => Task.FromResult(Array.Empty<float>());
        public Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token) => Task.FromResult(Array.Empty<byte>());
        public bool InitializeMixer(int outputSampleRate, int outputChannels, int frameDuration) => true;
        public bool TryBeginInitialGreeting(ActiveCallContext activeCall) => false;
        public void StartInitialGreeting(ActiveCallContext activeCall, Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt) { }
        public Task<bool> PlayCachedPromptAsync(ActiveCallContext activeCall, IReadOnlyList<string> relativeFilePaths, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> PlayFilePromptAsync(ActiveCallContext activeCall, string filePath, CancellationToken cancellationToken) => Task.FromResult(false);
        public void ProcessAudio(Agent.Telephone.Abstractions.Common.Enums.AudioType audioType, float[] audioData, string? sentenceId) { }
        public void CompleteStream(Agent.Telephone.Abstractions.Common.Enums.AudioType audioType) { }
        public void ClearAllBuffers() { }
        public void RegisterSubtitle(string sentenceId, Agent.Telephone.Abstractions.Common.Enums.AudioType audioType, Agent.Telephone.Abstractions.Common.Enums.TtsStatus ttsStatus, string text) { }
        public bool GetSubtitle(string sentenceId, out Agent.Telephone.Media.Abstractions.Dtos.AudioSubtitle subtitle) { subtitle = default; return false; }
        public Task AnalysisVoiceAsync(string deviceId, float[] newAudioData, float[] bufferedAudioData, CancellationToken token) => Task.CompletedTask;
        public void ResetSessionState(string deviceId) { }
        public Task ConvertSpeechTextAsync(Workflow<float[]> workflow, int sampleRate, CancellationToken token) => Task.CompletedTask;
        public Task ConvertSpeechTextStreamingAsync(
            Workflow<float[]> workflow,
            int sampleRate,
            Agent.Telephone.Providers.ASR.Contexts.StreamingAsrOperation operation,
            CancellationToken token) => Task.CompletedTask;
        public Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token) => Task.CompletedTask;
        public string? GetSavedAudioFilePath(string sentenceId) => null;
        public ValueTask AppendAssistantSegmentAsync(
            OfflineDialogueTurn turn,
            string textContent,
            string? audioPath,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask BeginOfflinePersistenceAsync(
            OfflineDialogueTurn turn,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask CompleteTurnAsync(
            OfflineDialogueTurn turn,
            bool read,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FailTurnAsync(
            OfflineDialogueTurn turn,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DiscardTurnAsync(
            OfflineDialogueTurn turn,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public Task FlushTurnAsync(
            OfflineDialogueTurn turn,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PersistCompletedOnlineTurnsAsync(
            ActiveCallContext activeCall,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartProactiveCallAsync(
            DeviceContext device,
            OfflineDialogueTurn turn) => Task.CompletedTask;
        public Task WaitForProactiveCallResultAsync(
            OfflineDialogueTurn turn,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopProactiveCallAsync(
            OfflineDialogueTurn turn) => Task.CompletedTask;
        public Task PlayAssistantMessageAsync(
            ActiveCallContext activeCall,
            string messageId,
            Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartInitialCallFlowAsync(ActiveCallContext activeCall, Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Agent.Telephone.Abstractions.Common.Contexts.AssistantSwitchResult> SwitchAssistantAsync(ActiveCallContext call, string targetAssistantNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Agent.Telephone.Abstractions.Common.Contexts.DtmfInputResult> RequestDtmfInputAsync(ActiveCallContext call, Agent.Telephone.Abstractions.Common.Enums.DtmfKey keys, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Agent.Telephone.Abstractions.Common.Enums.DtmfKey?> WaitForDtmfKeyAsync(ActiveCallContext call, Agent.Telephone.Abstractions.Common.Enums.DtmfKey keys, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void HandleDtmfTone(ActiveCallContext call, byte tone) { }

        public ValueTask<OfflineDialogueTurn> BeginTurnAsync(ActiveCallContext activeCall, long turnId, string userText, CancellationToken cancellationToken)
        {
            return default;
        }
    }

    private sealed class TrackingLlm : ILlm
    {
        public List<string> Events { get; } = [];
        public bool Build(Agent.Telephone.Common.Configs.LLMBuildConfig settings) => true;
        public void RegisterDevice(ActiveCallContext activeCall, ILlmEventCallback callback) { }
        public void UnregisterDevice(ActiveCallContext activeCall) => this.Events.Add("unregister");
        public Task StartDialogueAsync(long turnId, string userMessage, CancellationToken token) => Task.CompletedTask;
        public void Dispose() => this.Events.Add("dispose");
    }

    private sealed class TestCallSession : IDisposable
    {
        private readonly SIPTransport _transport;
        private readonly DeviceContext _device;
        private ActiveCallContext? _call;

        public TestCallSession(SIPTransport transport, DeviceContext device, ActiveCallContext call)
        {
            this._transport = transport;
            this._device = device;
            this._call = call;
        }

        public ActiveCallContext Call => this._call ?? throw new ObjectDisposedException(nameof(TestCallSession));

        public void Dispose()
        {
            ActiveCallContext? call = Interlocked.Exchange(ref this._call, null);
            if (call is not null)
            {
                this._device.CloseCallSession(call);
            }

            this._device.Dispose();
            this._transport.Dispose();
        }
    }
}
