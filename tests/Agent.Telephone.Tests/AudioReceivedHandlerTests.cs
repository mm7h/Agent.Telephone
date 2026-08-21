using System.Net;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Handlers.AIAdapterHandlers;
using Agent.Telephone.Handlers.SIPHandlers;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AudioReceivedHandlerTests
{
    private const string AssistantNumber = "2000";

    [Fact]
    public async Task OnLongTermSilence_HangsUpActiveCallAndClosesDeviceSessionAsync()
    {
        using SIPTransport serverTransport = new();
        using SIPTransport clientTransport = new();
        var serverChannel = new SIPUDPChannel(IPAddress.Loopback, 0);
        serverTransport.AddSIPChannel(serverChannel);
        using DeviceContext device = this.CreateDevice(serverTransport);
        var clientUserAgent = new SIPUserAgent(clientTransport, null);
        VoIPMediaSession clientMediaSession = this.CreateMediaSession();
        var callConnected = new TaskCompletionSource<ActiveCallContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var clientHungup = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Action<SIPDialogue> onClientHungup = _ => clientHungup.TrySetResult(true);
        clientUserAgent.OnCallHungup += onClientHungup;
        serverTransport.SIPTransportRequestReceived += OnRequest;
        try
        {
            SIPURI destination = serverChannel.GetContactURI(
                SIPSchemesEnum.sip,
                new SIPEndPoint(
                    SIPProtocolsEnum.udp,
                    new IPEndPoint(IPAddress.Loopback, 0)));
            destination.User = AssistantNumber;
            bool connected = await clientUserAgent.Call(
                destination.ToString(),
                null,
                null,
                clientMediaSession);
            Assert.True(connected);

            ActiveCallContext activeCall = await callConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var serverHungup = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<SIPDialogue> onServerHungup = _ => serverHungup.TrySetResult(true);
            activeCall.UserAgent.OnCallHungup += onServerHungup;
            var handler = new AudioReceivedHandler(
                rtpPacketWorkflowPool: null!,
                audioWorkflowPool: null!,
                this.CreateConfig(),
                NullLogger<AudioReceivedHandler>.Instance)
            {
                ActiveCallContext = activeCall
            };

            handler.OnLongTermSilence();

            await serverHungup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clientHungup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(activeCall.UserAgent.IsCallActive);
            Assert.Null(device.ActiveCall);
            Assert.Equal(CallState.Idle, device.CallState);

            handler.OnLongTermSilence();
            activeCall.UserAgent.OnCallHungup -= onServerHungup;
        }
        finally
        {
            serverTransport.SIPTransportRequestReceived -= OnRequest;
            clientUserAgent.OnCallHungup -= onClientHungup;
            if (device.ActiveCall is ActiveCallContext activeCall)
            {
                device.CloseCallSession(activeCall);
            }

            if (clientUserAgent.IsCallActive)
            {
                clientUserAgent.Hangup();
            }

            clientMediaSession.Close("test ended");
            serverTransport.Shutdown();
            clientTransport.Shutdown();
        }

        async Task OnRequest(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
        {
            if (request.Method != SIPMethodsEnum.INVITE)
            {
                return;
            }

            Assert.True(device.TryInitializeCallSession(request, out ActiveCallContext? activeCall));
            Assert.NotNull(activeCall);
            var activeCallHandler = new ActiveCallHandler(
                this.CreateConfig(),
                NullLogger<ActiveCallHandler>.Instance)
            {
                ActiveCallContext = activeCall
            };
            Assert.True(activeCallHandler.Build());
            activeCall.RegisterCallOwnedResource(activeCallHandler);

            SIPServerUserAgent serverUserAgent = Assert.IsType<SIPServerUserAgent>(
                device.TakePendingServerUserAgent(activeCall));
            bool answered = await activeCall.UserAgent.Answer(serverUserAgent, activeCall.VoIPRTP);
            Assert.True(answered);
            device.MarkCallConnected(activeCall);
            callConnected.TrySetResult(activeCall);
        }
    }

    private DeviceContext CreateDevice(SIPTransport transport)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var registration = new DeviceRegistrationRecord(
            "device-1",
            "sip:1001@device.test",
            "sip:1001@127.0.0.1:5060",
            now,
            now,
            now.AddMinutes(5));
        return new DeviceContext(
            transport,
            registration,
            [new AssistantConfig { DialingNumber = AssistantNumber }]);
    }

    private VoIPMediaSession CreateMediaSession()
    {
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        return new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
    }

    private TelephoneConfig CreateConfig() => new()
    {
        AuthEnabled = false,
        SIPConfig = new SIPConfig(),
        AssistantConfigs = [new AssistantConfig { DialingNumber = AssistantNumber }],
        ModelConfig = new ModelConfig()
    };
}
