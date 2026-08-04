using System.Net;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using Agent.Telephone.Management;
using Agent.Telephone.Protocol.Server.Middlewares;
using Agent.Telephone.Providers.CallControl.Reservations;
using Agent.Telephone.Providers.Conversation;
using Agent.Telephone.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class SipRegistrarInboundTests
{
    private const string DEVICE_NUMBER = "1001";
    private const string ASSISTANT_NUMBER = "2000";
    private const string TARGET_ASSISTANT_NUMBER = "2001";

    [Fact]
    public void RequestIdentityUsesFromAorAndAssistantUsesRequestUri()
    {
        SIPRequest request = CreateRequest(
            SIPMethodsEnum.INVITE,
            DEVICE_NUMBER,
            ASSISTANT_NUMBER,
            "9999");

        Assert.Contains("1001@device.test", request.GetDeviceId(), StringComparison.Ordinal);
        Assert.Equal(DEVICE_NUMBER, request.GetCallerAor().User);
        Assert.Equal(ASSISTANT_NUMBER, request.GetAssistantNumber());
        Assert.NotEqual(request.Header.To.ToURI.User, request.GetAssistantNumber());
    }

    [Fact]
    public async Task RegistrationRefreshUnregisterAndExpiryUpdateStatesAsync()
    {
        using SIPTransport transport = new();
        using DefaultMemoryStore store = new();
        TelephoneConfig config = CreateConfig();
        DeviceContextManager manager = CreateDeviceManager(store, config);

        SIPRequest initial = CreateRegisterRequest("sip:1001@192.0.2.10:5060", 300, 120);
        (SIPURI initialContact, int initialExpires) = initial.GetRegistration();
        Assert.Equal(120, initialExpires);

        await manager.OnSIPDeviceRegisteringAsync(transport, initial);
        DeviceContext device = Assert.IsType<DeviceContext>(manager.GetSIPDeviceById(initial));
        Assert.Equal(RegistrationState.Registered, device.RegistrationState);
        Assert.Equal("192.0.2.10:5060", device.Registration!.Contact.Host);
        DateTimeOffset registeredAt = device.Registration.RegisteredAt;

        SIPRequest refresh = CreateRegisterRequest("sip:1001@192.0.2.11:5070", 600, 240);
        await manager.OnSIPDeviceRegisteringAsync(transport, refresh);
        Assert.Same(device, manager.GetSIPDeviceById(refresh));
        Assert.Equal(RegistrationState.Registered, device.RegistrationState);
        Assert.Equal("192.0.2.11:5070", device.Registration!.Contact.Host);
        Assert.Equal(registeredAt, device.Registration.RegisteredAt);
        Assert.True(device.Registration.RefreshedAt >= registeredAt);

        SIPRequest unregister = CreateRegisterRequest("sip:1001@192.0.2.11:5070", 600, 0);
        await manager.OnSIPDeviceRegisteringAsync(transport, unregister);
        Assert.Equal(RegistrationState.Offline, device.RegistrationState);
        Assert.Null(device.Registration);
        Assert.Null(manager.GetRegisteredSIPDeviceById(unregister));

        SIPRequest shortRegistration = CreateRegisterRequest("sip:1001@192.0.2.12:5080", 1, 1);
        await manager.OnSIPDeviceRegisteringAsync(transport, shortRegistration);
        Assert.Equal(RegistrationState.Registered, device.RegistrationState);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Assert.False(device.IsRegistered());
        Assert.Equal(RegistrationState.Expired, device.RegistrationState);
        Assert.Null(manager.GetRegisteredSIPDeviceById(shortRegistration));
    }

    [Fact]
    public void CallbackOccupationTransitionsCallStateAndPreventsSecondOccupation()
    {
        using SIPTransport transport = new();
        SIPRequest register = CreateRegisterRequest("sip:1001@192.0.2.10:5060", 300, 300);
        using var device = new DeviceContext(
            transport,
            register,
            SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060"),
            300,
            CreateConfig().AssistantConfigs);

        Assert.Equal(CallState.Idle, device.CallState);
        Assert.True(device.TryBeginCallback());
        Assert.Equal(CallState.CallbackDialing, device.CallState);
        Assert.False(device.TryBeginCallback());

        device.MarkCallbackConnected();
        Assert.Equal(CallState.CallbackConnected, device.CallState);

        device.EndCallback();
        Assert.Equal(CallState.Idle, device.CallState);
    }

    [Fact]
    public void CallbackCanAttachOutboundMediaAsActiveAgentCall()
    {
        using SIPTransport transport = new();
        SIPRequest register = CreateRegisterRequest("sip:1001@192.0.2.10:5060", 300, 300);
        using var device = new DeviceContext(
            transport,
            register,
            SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060"),
            300,
            CreateConfig().AssistantConfigs);
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(
            new MediaEndPoints { AudioSource = source });

        Assert.True(device.TryBeginCallback());
        Assert.True(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            ASSISTANT_NUMBER,
            userAgent,
            mediaSession,
            out ActiveCallContext? activeCall));
        Assert.NotNull(activeCall);
        Assert.Same(activeCall, device.ActiveCall);
        Assert.Equal("sip:1001@device.test", activeCall.UserAor);
        Assert.Equal(ASSISTANT_NUMBER, activeCall.DialedNumber);
        Assert.Equal(CallState.CallbackConnected, device.CallState);
        Assert.False(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            ASSISTANT_NUMBER,
            userAgent,
            mediaSession,
            out _));

        activeCall.MarkPlayingPrompt();
        Assert.Equal(CallState.PlayingPrompt, device.CallState);
        activeCall.MarkEnding();
        Assert.Equal(CallState.Ending, device.CallState);

        device.CloseCallSession(activeCall);
        device.EndCallback();
        Assert.Null(device.ActiveCall);
        Assert.Equal(CallState.Idle, device.CallState);
    }

    [Fact]
    public void AssistantSwitchReplacesOnlyTheAgentSession()
    {
        using SIPTransport transport = new();
        SIPRequest register = CreateRegisterRequest("sip:1001@192.0.2.10:5060", 300, 300);
        using var device = new DeviceContext(
            transport,
            register,
            SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060"),
            300,
            CreateConfig().AssistantConfigs);
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });

        Assert.True(device.TryBeginCallback());
        Assert.True(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            ASSISTANT_NUMBER,
            userAgent,
            mediaSession,
            out ActiveCallContext? activeCall));
        Assert.NotNull(activeCall);

        AIAgentContext originalAgent = activeCall.AIAgentContext;
        string originalCallId = activeCall.CallId;
        var originalResource = new TrackingDisposable();
        var callOwnedResource = new TrackingDisposable();
        originalAgent.RegisterOwnedResource(originalResource);
        activeCall.RegisterCallOwnedResource(callOwnedResource);

        Assert.True(activeCall.TryBeginAssistantSwitch());
        Assert.False(activeCall.TryBeginAssistantSwitch());
        Assert.True(activeCall.IsAgentSwitching);
        Assert.True(activeCall.IsAgentMediaPaused);
        Assert.True(activeCall.TryReplaceAssistantSession(TARGET_ASSISTANT_NUMBER));

        Assert.True(originalResource.IsDisposed);
        Assert.False(callOwnedResource.IsDisposed);
        Assert.NotSame(originalAgent, activeCall.AIAgentContext);
        Assert.Equal(TARGET_ASSISTANT_NUMBER, activeCall.DialedNumber);
        Assert.Equal(TARGET_ASSISTANT_NUMBER, activeCall.AssistantConfig.DialingNumber);
        Assert.Equal(originalCallId, activeCall.CallId);
        Assert.Same(userAgent, activeCall.UserAgent);
        Assert.Same(mediaSession, activeCall.VoIPRTP);

        activeCall.CompleteAssistantSwitch();
        activeCall.ResumeAgentMedia();
        Assert.False(activeCall.IsAgentSwitching);
        Assert.False(activeCall.IsAgentMediaPaused);

        device.CloseCallSession(activeCall);
        Assert.True(callOwnedResource.IsDisposed);
        device.EndCallback();
    }

    [Fact]
    public async Task ExpiredRegistrationCannotAttachAnsweredCallbackAsync()
    {
        using SIPTransport transport = new();
        using DefaultMemoryStore store = new();
        TelephoneConfig config = CreateConfig();
        DeviceContextManager manager = CreateDeviceManager(store, config);
        SIPRequest register = CreateRegisterRequest(
            "sip:1001@192.0.2.10:5060",
            1,
            1);
        await manager.OnSIPDeviceRegisteringAsync(transport, register);

        RegisteredEndpointResolution resolution = await manager.AcquireCallbackAsync(
            "sip:1001@device.test",
            CancellationToken.None);
        Assert.Equal(RegisteredEndpointStatus.Available, resolution.Status);
        using IRegisteredEndpointLease lease = Assert.IsAssignableFrom<IRegisteredEndpointLease>(
            resolution.Lease);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(
            new MediaEndPoints { AudioSource = source });
        try
        {
            Assert.False(manager.TryAttachCallbackCallSession(
                "sip:1001@device.test",
                ASSISTANT_NUMBER,
                userAgent,
                mediaSession,
                out _,
                out _));
        }
        finally
        {
            mediaSession.Close("test ended");
        }
    }

    [Fact]
    public async Task UdpInboundRejectsUnregisteredUnknownAssistantUnsupportedCodecAndBusyDeviceAsync()
    {
        TelephoneConfig config = CreateConfig();
        using DefaultMemoryStore store = new();
        using SIPTransport serverTransport = new();
        using SIPTransport clientTransport = new();
        var serverChannel = new SIPUDPChannel(IPAddress.Loopback, 0);
        var clientChannel = new SIPUDPChannel(IPAddress.Loopback, 0);
        serverTransport.AddSIPChannel(serverChannel);
        clientTransport.AddSIPChannel(clientChannel);

        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        DeviceContextManager deviceManager = CreateDeviceManager(store, config, services);
        ConversationProvider conversationProvider = new(
            null!,
            null!,
            null!,
            null!,
            NullLogger<ConversationProvider>.Instance);
        FunctionToolManager functionTools = new(
            NullLoggerFactory.Instance,
            services,
            config,
            NullLogger<FunctionToolManager>.Instance);
        HandlerManager handlers = new(
            services,
            config,
            NullLogger<HandlerManager>.Instance);
        ProviderManager providers = new(
            services,
            config,
            conversationProvider,
            NullLogger<ProviderManager>.Instance);
        var middleware = new DeviceContainerMiddleware(
            services,
            config,
            deviceManager,
            functionTools,
            handlers,
            providers,
            conversationProvider,
            NullLogger<DeviceContainerMiddleware>.Instance);
        middleware.SubscribeSIPTransportEvents(serverTransport);

        try
        {
            SIPEndPoint serverEndPoint = serverChannel.ListeningSIPEndPoint;

            SIPResponse unregistered = await SendAndWaitForFinalResponseAsync(
                clientTransport,
                serverEndPoint,
                CreateInviteRequest(ASSISTANT_NUMBER, "0"));
            Assert.Equal(SIPResponseStatusCodesEnum.Forbidden, unregistered.Status);

            SIPRequest register = CreateRegisterRequest(
                $"sip:{DEVICE_NUMBER}@{clientChannel.ListeningSIPEndPoint.GetIPEndPoint()}",
                300,
                300);
            SIPResponse registered = await SendAndWaitForFinalResponseAsync(
                clientTransport,
                serverEndPoint,
                register);
            Assert.Equal(SIPResponseStatusCodesEnum.Ok, registered.Status);

            SIPResponse unknownAssistant = await SendAndWaitForFinalResponseAsync(
                clientTransport,
                serverEndPoint,
                CreateInviteRequest("2999", "0"));
            Assert.Equal(SIPResponseStatusCodesEnum.NotFound, unknownAssistant.Status);

            SIPResponse unsupportedCodec = await SendAndWaitForFinalResponseAsync(
                clientTransport,
                serverEndPoint,
                CreateInviteRequest(ASSISTANT_NUMBER, "111"));
            Assert.Equal(SIPResponseStatusCodesEnum.NotAcceptableHere, unsupportedCodec.Status);

            DeviceContext device = Assert.IsType<DeviceContext>(deviceManager.GetSIPDeviceById(register));
            Assert.True(device.TryBeginCallback());
            SIPResponse busy = await SendAndWaitForFinalResponseAsync(
                clientTransport,
                serverEndPoint,
                CreateInviteRequest(ASSISTANT_NUMBER, "0"));
            Assert.Equal(SIPResponseStatusCodesEnum.BusyHere, busy.Status);
            device.EndCallback();
        }
        finally
        {
            middleware.UnsubscribeSIPTransportEvents(serverTransport);
            serverTransport.Shutdown();
            clientTransport.Shutdown();
        }
    }

    private static DeviceContextManager CreateDeviceManager(
        DefaultMemoryStore store,
        TelephoneConfig config,
        IServiceProvider? services = null)
    {
        IServiceProvider serviceProvider = services ?? new ServiceCollection()
            .BuildServiceProvider();
        return new DeviceContextManager(
            store,
            serviceProvider,
            new TransferReservationRegistry(),
            config,
            NullLogger<DeviceContextManager>.Instance);
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

    private static SIPRequest CreateRegisterRequest(
        string contactUri,
        int headerExpires,
        long contactExpires)
    {
        SIPRequest request = CreateRequest(
            SIPMethodsEnum.REGISTER,
            DEVICE_NUMBER,
            "registrar",
            DEVICE_NUMBER);
        var contact = new SIPContactHeader(null, SIPURI.ParseSIPURI(contactUri))
        {
            Expires = contactExpires
        };
        request.Header.Contact = [contact];
        request.Header.Expires = headerExpires;
        return request;
    }

    private static SIPRequest CreateInviteRequest(string assistantNumber, string payloadId)
    {
        SIPRequest request = CreateRequest(
            SIPMethodsEnum.INVITE,
            DEVICE_NUMBER,
            assistantNumber,
            assistantNumber);
        request.Header.ContentType = SDP.SDP_MIME_CONTENTTYPE;
        request.Body =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n" +
            $"m=audio 40000 RTP/AVP {payloadId}\r\n" +
            (payloadId == "0" ? "a=rtpmap:0 PCMU/8000\r\n" : "a=rtpmap:111 opus/48000/2\r\n");
        request.Header.ContentLength = request.Body.Length;
        return request;
    }

    private static SIPRequest CreateRequest(
        SIPMethodsEnum method,
        string callerNumber,
        string requestUser,
        string toUser)
    {
        SIPURI requestUri = SIPURI.ParseSIPURI($"sip:{requestUser}@127.0.0.1");
        SIPRequest request = SIPRequest.GetRequest(method, requestUri);
        request.Header.From = new SIPFromHeader(
            null,
            SIPURI.ParseSIPURI($"sip:{callerNumber}@device.test"),
            CallProperties.CreateNewTag());
        request.Header.To = new SIPToHeader(
            null,
            SIPURI.ParseSIPURI($"sip:{toUser}@server.test"),
            null);
        return request;
    }

    private static async Task<SIPResponse> SendAndWaitForFinalResponseAsync(
        SIPTransport clientTransport,
        SIPEndPoint serverEndPoint,
        SIPRequest request)
    {
        var completion = new TaskCompletionSource<SIPResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnResponse(SIPEndPoint local, SIPEndPoint remote, SIPResponse response)
        {
            if (response.StatusCode >= 200 &&
                string.Equals(
                    response.Header.CallId,
                    request.Header.CallId,
                    StringComparison.Ordinal))
            {
                completion.TrySetResult(response);
            }

            return Task.CompletedTask;
        }

        clientTransport.SIPTransportResponseReceived += OnResponse;
        try
        {
            await clientTransport.SendRequestAsync(serverEndPoint, request);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            clientTransport.SIPTransportResponseReceived -= OnResponse;
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            this.IsDisposed = true;
        }
    }
}
