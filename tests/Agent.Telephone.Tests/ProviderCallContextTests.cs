using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class ProviderCallContextTests
{
    [Fact]
    public void UnregisterDevice_OnlyClearsMatchingCurrentCall()
    {
        using SIPTransport transport = new();
        using DeviceContext firstDevice = this.CreateDevice(transport, "device-1");
        using DeviceContext secondDevice = this.CreateDevice(transport, "device-2");
        using ActiveCallContext firstCall = this.CreateCall(transport, firstDevice);
        using ActiveCallContext secondCall = this.CreateCall(transport, secondDevice);
        var provider = new TestProvider();

        provider.RegisterDevice(firstCall);
        provider.RegisterDevice(secondCall);
        provider.UnregisterDevice(firstCall);

        Assert.Same(secondCall, provider.RegisteredCall);

        provider.UnregisterDevice(secondCall);

        Assert.Null(provider.RegisteredCall);
    }

    [Fact]
    public void NegotiatedG711FormatUsesItsSampleRateForAudioSaving()
    {
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport, "device-1");
        using ActiveCallContext call = this.CreateCall(transport, device);
        var provider = new TestProvider();
        call.NegotiatedAudioFormat = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);

        provider.RegisterDevice(call);

        Assert.Equal(8000, provider.AudioSavingSampleRate);
    }

    [Fact]
    public void CallbackCallUsesTheEstablishedMediaFormat()
    {
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport, "device-1");
        VoIPMediaSession callbackMediaSession = CreateMediaSession();
        VoIPMediaSession phoneMediaSession = CreateMediaSession();
        SDP offer = callbackMediaSession.CreateOffer();

        Assert.Equal(SetDescriptionResultEnum.OK, phoneMediaSession.SetRemoteDescription(SdpType.offer, offer));
        SDP answer = phoneMediaSession.CreateAnswer(System.Net.IPAddress.Loopback);
        Assert.Equal(SetDescriptionResultEnum.OK, callbackMediaSession.SetRemoteDescription(SdpType.answer, answer));

        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        using var callbackCall = new ActiveCallContext(
            device,
            "sip:1001@device.test",
            "10000",
            userAgent,
            callbackMediaSession);

        AudioFormat expected = callbackMediaSession.AudioStream.GetSendingFormat().ToAudioFormat();
        Assert.Equal(expected, callbackCall.NegotiatedAudioFormat);
        Assert.Contains(callbackCall.NegotiatedAudioFormat.Codec, SupportedAudioFormats.SupportedAudioCodecs);
    }

    [Fact]
    public void CallDisposalWaitsForBackgroundReplyLeaseAndKeepsDeviceBusy()
    {
        using SIPTransport transport = new();
        using DeviceContext device = this.CreateDevice(transport, "device-1");
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

        var resource = new TrackingDisposable();
        call.RegisterCallOwnedResource(resource);
        Assert.True(call.TryAcquireUse(out IDisposable? lease));
        Assert.NotNull(lease);
        Assert.True(device.TryBeginBackgroundReply(call));

        device.CloseCallSession(call);

        Assert.True(device.IsCallOccupied);
        Assert.False(resource.IsDisposed);
        Assert.True(call.CallToken.IsCancellationRequested);

        lease.Dispose();
        Assert.True(SpinWait.SpinUntil(() => resource.IsDisposed, TimeSpan.FromSeconds(1)));
        device.EndBackgroundReply();
        Assert.False(device.IsCallOccupied);
    }

    private DeviceContext CreateDevice(SIPTransport transport, string deviceId)
    {
        DeviceRegistrationRecord registration = new(
            deviceId,
            $"sip:{deviceId}@device.test",
            $"sip:{deviceId}@192.0.2.10:5060",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));
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

    private static VoIPMediaSession CreateMediaSession()
    {
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        return new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
    }

    private sealed class TestProvider : BaseProvider<TestProvider, ModelSetting>
    {
        public TestProvider() : base(NullLogger<TestProvider>.Instance)
        {
        }

        public override string ProviderType => "test";

        public override string ModelName => "test";

        public ActiveCallContext? RegisteredCall => this.CurrentCall;

        public int AudioSavingSampleRate => this.GetNegotiatedAudioSavingSampleRate();

        public override bool Build(ModelSetting settings) => true;

        public override void Dispose()
        {
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
