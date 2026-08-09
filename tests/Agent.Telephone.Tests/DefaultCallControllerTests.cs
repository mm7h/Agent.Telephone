using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.CallControl;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class DefaultCallControllerTests
{
    private const string DEVICE_NUMBER = "1001";
    private const string ASSISTANT_NUMBER = "2000";
    private const string TARGET_ASSISTANT_NUMBER = "2001";

    [Fact]
    public async Task SwitchAssistantAsync_RejectsEmptyTargetAsync()
    {
        using TestCallSession session = CreateActiveCall();
        AssistantRoleControl controller = CreateController();

        AssistantSwitchResult result = await controller.SwitchAssistantAsync(
            session.Call,
            "  ");

        Assert.Equal(AssistantSwitchStatus.InvalidTarget, result.Status);
    }

    [Fact]
    public async Task SwitchAssistantAsync_RejectsUnknownAssistantAsync()
    {
        using TestCallSession session = CreateActiveCall();
        AssistantRoleControl controller = CreateController();

        AssistantSwitchResult result = await controller.SwitchAssistantAsync(
            session.Call,
            "2999");

        Assert.Equal(AssistantSwitchStatus.UnknownAssistant, result.Status);
    }

    [Fact]
    public async Task SwitchAssistantAsync_RejectsCurrentAssistantAsync()
    {
        using TestCallSession session = CreateActiveCall();
        AssistantRoleControl controller = CreateController();

        AssistantSwitchResult result = await controller.SwitchAssistantAsync(
            session.Call,
            ASSISTANT_NUMBER);

        Assert.Equal(AssistantSwitchStatus.CurrentAssistant, result.Status);
    }

    [Fact]
    public async Task SwitchAssistantAsync_RejectsInactiveCallAsync()
    {
        using TestCallSession session = CreateActiveCall();
        AssistantRoleControl controller = CreateController();

        AssistantSwitchResult result = await controller.SwitchAssistantAsync(
            session.Call,
            TARGET_ASSISTANT_NUMBER);

        Assert.Equal(AssistantSwitchStatus.CallEnded, result.Status);
    }

    private static AssistantRoleControl CreateController()
    {
        AssistantRoleControl controller = new(
            functionToolManager: null!,
            providerManager: null!,
            handlerManager: null!,
            audioPromptPlayer: null!,
            NullLogger<AssistantRoleControl>.Instance);
        Assert.True(controller.Build(CreateConfig().AssistantConfigs));
        return controller;
    }

    private static TestCallSession CreateActiveCall()
    {
        var transport = new SIPTransport();
        SIPRequest register = CreateRegisterRequest(
            $"sip:{DEVICE_NUMBER}@192.0.2.10:5060",
            300);
        var device = new DeviceContext(
            transport,
            register,
            SIPURI.ParseSIPURI($"sip:{DEVICE_NUMBER}@192.0.2.10:5060"),
            300,
            CreateConfig().AssistantConfigs);

        Assert.True(device.TryBeginCallback());
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(
            new MediaEndPoints { AudioSource = source });
        Assert.True(device.TryAttachCallbackCallSession(
            $"sip:{DEVICE_NUMBER}@device.test",
            ASSISTANT_NUMBER,
            userAgent,
            mediaSession,
            out ActiveCallContext? activeCall));
        Assert.NotNull(activeCall);

        device.EndCallback();
        return new TestCallSession(transport, device, activeCall);
    }

    private static TelephoneConfig CreateConfig() => new()
    {
        AuthEnabled = false,
        SIPConfig = new SIPConfig(),
        AssistantConfigs =
        [
            new AssistantConfig
            {
                DialingNumber = ASSISTANT_NUMBER,
                Name = "test-assistant"
            },
            new AssistantConfig
            {
                DialingNumber = TARGET_ASSISTANT_NUMBER,
                Name = "target-assistant"
            }
        ],
        ModelConfig = new ModelConfig()
    };

    private static SIPRequest CreateRegisterRequest(string contactUri, int expires)
    {
        SIPRequest request = SIPRequest.GetRequest(
            SIPMethodsEnum.REGISTER,
            SIPURI.ParseSIPURI("sip:registrar@127.0.0.1"));
        request.Header.From = new SIPFromHeader(
            null,
            SIPURI.ParseSIPURI($"sip:{DEVICE_NUMBER}@device.test"),
            CallProperties.CreateNewTag());
        request.Header.To = new SIPToHeader(
            null,
            SIPURI.ParseSIPURI("sip:registrar@server.test"),
            null);
        var contact = new SIPContactHeader(
            null,
            SIPURI.ParseSIPURI(contactUri))
        {
            Expires = expires
        };
        request.Header.Contact = [contact];
        request.Header.Expires = expires;
        return request;
    }

    private sealed class TestCallSession : IDisposable
    {
        private readonly SIPTransport _transport;
        private readonly DeviceContext _device;
        private ActiveCallContext? _call;

        public TestCallSession(
            SIPTransport transport,
            DeviceContext device,
            ActiveCallContext call)
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
