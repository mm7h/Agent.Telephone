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

namespace Agent.Telephone.Tests;

public sealed class DeferredHangupTests
{
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
        audio2Text.OnSpeechTextConverted(success: true, text: "再见");

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
