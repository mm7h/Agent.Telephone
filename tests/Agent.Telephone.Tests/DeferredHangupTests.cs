using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers.AIAdapterHandlers;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;
using System.Reflection;
using System.Threading.Channels;
using Agent.Telephone.Common.Enums;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Tests;

public sealed class DeferredHangupTests
{
    [Fact]
    public async Task Farewell_DoesNotEmitTerminalMarkerBeforeSynthesisCompletesAsync()
    {
        using TestCallSession session = CreateCall();
        Channel<Workflow<OutAudioSegment>> output = Channel.CreateUnbounded<Workflow<OutAudioSegment>>();
        using Text2AudioHandler handler = new(
            null!,
            null!,
            new DefaultObjectPool<Workflow<OutAudioSegment>>(new DefaultPooledObjectPolicy<Workflow<OutAudioSegment>>()),
            new DefaultObjectPool<OutAudioSegment>(new DefaultPooledObjectPolicy<OutAudioSegment>()),
            CreateConfig(),
            NullLogger<Text2AudioHandler>.Instance)
        {
            ActiveCallContext = session.Call,
            NextWriter = output.Writer,
        };

        handler.OnBeforeProcessing("好的。再见", isFirstSegment: true, isLastSegment: true);
        Workflow<OutAudioSegment> start = await output.Reader.ReadAsync();
        Assert.True(start.Data.IsFirstSegment);
        Assert.False(start.Data.IsLastSegment);

        handler.OnProcessing([0.1f, 0.2f], isFirstFrame: true, isLastFrame: true);
        Workflow<OutAudioSegment> audio = await output.Reader.ReadAsync();
        Assert.Equal(new[] { 0.1f, 0.2f }, audio.Data.AudioData);
        Assert.False(audio.Data.IsLastSegment);
        Assert.False(output.Reader.TryRead(out _));

        handler.OnProcessed("好的。再见", true, true, TtsGenerateResult.Success);
        Workflow<OutAudioSegment> end = await output.Reader.ReadAsync();
        Assert.True(end.Data.IsLastSegment);
        Assert.True(end.Data.IsLastFrame);
    }

    [Fact]
    public async Task Farewell_WaitsForLastRtpPacketBeforeCompletingHangupAsync()
    {
        using TestCallSession session = CreateCall();
        Assert.True(session.Call.TryBeginHangupAfterReply());
        using AudioSendHandler handler = new(null!, null!, CreateConfig(), NullLogger<AudioSendHandler>.Instance)
        {
            ActiveCallContext = session.Call,
        };
        MethodInfo markPlayback = typeof(AudioSendHandler).GetMethod("MarkFinalPlaybackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MixedAudioPacket packet = new();
        packet.Initialize([0.1f], isFirstFrame: true, isLastFrame: false);
        Workflow<MixedAudioPacket> workflow = new();
        workflow.Initialize(session.Call, packet);
        await (Task)markPlayback.Invoke(handler, [workflow, true])!;
        Assert.True(session.Call.IsHangupAfterReplyPending(session.Call.TurnId));

        packet.Initialize([], isFirstFrame: false, isLastFrame: true);
        await (Task)markPlayback.Invoke(handler, [workflow, true])!;
        Assert.False(session.Call.IsHangupAfterReplyPending(session.Call.TurnId));
    }

    [Fact]
    public async Task ToolPrompt_WaitsForPlaybackAndHonorsTurnCancellationAsync()
    {
        using TestCallSession session = CreateCall();
        session.Call.AIAgentContext.SetPromptSynthesizer((text, paragraph, sentence, token) => Task.FromResult(true));
        Task<bool> playing = session.Call.AIAgentContext.PlayToolExecutionPromptAsync("正在处理", CancellationToken.None);
        Assert.True(session.Call.IsPromptPlaybackPending);
        Assert.False(playing.IsCompleted);
        session.Call.CompletePromptPlayback(fullyPlayed: true);
        Assert.True(await playing);

        using CancellationTokenSource cancellation = new();
        playing = session.Call.AIAgentContext.PlayToolExecutionPromptAsync("正在处理", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playing);
        Assert.False(session.Call.IsPromptPlaybackPending);
    }

    [Fact]
    public async Task ToolPrompt_CallEndedStopsPlaybackWithoutCancellingGenerationAsync()
    {
        using TestCallSession session = CreateCall();
        session.Call.AIAgentContext.SetPromptSynthesizer((text, paragraph, sentence, token) => Task.FromResult(true));
        Task<bool> playing = session.Call.AIAgentContext.PlayToolExecutionPromptAsync("正在处理", CancellationToken.None);
        session.Call.Cancel();
        Assert.False(await playing);
        Assert.False(session.Call.IsPromptPlaybackPending);
    }

    [Fact]
    public void HangupAfterReply_PausesInputAndOnlyCompletesTheMatchingTurn()
    {
        using TestCallSession session = CreateCall();
        session.Call.RestartTurn();
        long turnId = session.Call.TurnId;

        Assert.True(session.Call.TryBeginHangupAfterReply());
        Assert.True(session.Call.IsUserAudioInputPaused);
        Assert.True(session.Call.IsHangupAfterReplyPending(turnId));
        Assert.False(session.Call.TryBeginHangupAfterReply());
        Assert.False(session.Call.CompleteHangupAfterReply(turnId + 1));
        Assert.True(session.Call.IsHangupAfterReplyPending(turnId));

        session.Call.CompleteHangupAfterReply(turnId);

        Assert.False(session.Call.IsHangupAfterReplyPending(turnId));
    }

    [Fact]
    public void UserAudioProcessing_IsReleasedOnlyByTheOwningTurn()
    {
        using TestCallSession session = CreateCall();
        session.Call.RestartTurn();
        long turnId = session.Call.TurnId;

        session.Call.BeginUserAudioProcessing(turnId);

        Assert.True(session.Call.IsUserAudioProcessing);
        Assert.False(session.Call.CompleteUserAudioProcessing(turnId + 1));
        Assert.True(session.Call.IsUserAudioProcessing);
        Assert.True(session.Call.CompleteUserAudioProcessing(turnId));
        Assert.False(session.Call.IsUserAudioProcessing);
    }

    [Fact]
    public void StaleAsrCallback_DoesNotInterruptTheCurrentTurnOrReleaseItsInputLock()
    {
        using TestCallSession session = CreateCall();
        session.Call.RestartTurn();
        long currentTurnId = session.Call.TurnId;
        session.Call.BeginUserAudioProcessing(currentTurnId);
        var audio2Text = new Audio2TextHandler(
            audioWorkflowPool: null!,
            textWorkflowPool: null!,
            CreateConfig(),
            NullLogger<Audio2TextHandler>.Instance)
        {
            ActiveCallContext = session.Call
        };

        audio2Text.OnSpeechTextConverted(currentTurnId - 1, success: true, text: "过期结果");

        Assert.Equal(currentTurnId, session.Call.TurnId);
        Assert.True(session.Call.IsUserAudioProcessing);
    }

    [Fact]
    public void PausedInput_DiscardsQueuedVadAndAsrCallbacks()
    {
        using TestCallSession session = CreateCall();
        session.Call.RestartTurn();
        long turnId = session.Call.TurnId;
        session.Call.PauseUserAudioInput();

        var audioReceived = new AudioReceivedHandler(
            rtpPacketWorkflowPool: null!,
            audioWorkflowPool: null!,
            CreateConfig(),
            NullLogger<AudioReceivedHandler>.Instance)
        {
            ActiveCallContext = session.Call
        };
        var audio2Text = new Audio2TextHandler(
            audioWorkflowPool: null!,
            textWorkflowPool: null!,
            CreateConfig(),
            NullLogger<Audio2TextHandler>.Instance)
        {
            ActiveCallContext = session.Call
        };

        audioReceived.OnVoiceDetected(new float[50]);
        audio2Text.OnSpeechTextConverted(turnId, success: true, text: "再见");

        Assert.Equal(turnId, session.Call.TurnId);
    }

    private static TelephoneConfig CreateConfig() => new()
    {
        SIPConfig = new SIPConfig(),
        AssistantConfigs = [new AssistantConfig { DialingNumber = "2000" }],
        ModelConfig = new ModelConfig(),
    };

    private static TestCallSession CreateCall()
    {
        var transport = new SIPTransport();
        var device = new DeviceContext(
            TestServices.ScopeFactory,
            transport,
            new DeviceRegistrationRecord(
                "device-1",
                "sip:1001@device.test",
                "sip:1001@192.0.2.10:5060",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            CreateConfig().AssistantConfigs);
        Assert.True(device.TryBeginCallback());
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        Assert.True(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            "2000",
            userAgent,
            mediaSession,
            out ActiveCallContext? call));
        Assert.NotNull(call);
        device.EndCallback();
        return new TestCallSession(transport, device, call);
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

        public ActiveCallContext Call => this._call
            ?? throw new ObjectDisposedException(nameof(TestCallSession));

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
