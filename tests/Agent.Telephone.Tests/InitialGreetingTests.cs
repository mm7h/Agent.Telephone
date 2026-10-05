using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Management;
using Agent.Telephone.Providers.AudioProcessor;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class InitialGreetingTests
{
    [Fact]
    public void GetGreetingAudioFiles_UsesFourRandomDigitsThenServicePrompt()
    {
        IReadOnlyList<string> audioFiles = DefaultAudioProcessor.GetGreetingAudioFiles();

        Assert.Equal(5, audioFiles.Count);
        Assert.All(audioFiles.Take(4), audioFile => Assert.Matches(@"^numbers/[0-9]\.mp3$", audioFile));
        Assert.Equal("prompt/service_agent.mp3", audioFiles[4]);
    }

    [Fact]
    public void HasInitialGreeting_AcceptsEveryAssistantWithTemplatesAndNumericNumber()
    {
        var customer = new AssistantConfig
        {
            DialingNumber = "10000",
            HelloMessageTempletes = ["您好"]
        };
        var otherAssistant = new AssistantConfig
        {
            DialingNumber = "10086",
            HelloMessageTempletes = ["您好"]
        };

        Assert.True(DefaultAudioProcessor.HasInitialGreeting(customer));
        Assert.True(DefaultAudioProcessor.HasInitialGreeting(otherAssistant));
    }

    [Theory]
    [InlineData("10000", new[] { " " })]
    [InlineData("100-00", new[] { "您好" })]
    public void HasInitialGreeting_RejectsMissingTemplateOrNonNumericNumber(
        string dialingNumber,
        string[] templates)
    {
        var assistant = new AssistantConfig
        {
            DialingNumber = dialingNumber,
            HelloMessageTempletes = templates.ToList()
        };

        Assert.False(DefaultAudioProcessor.HasInitialGreeting(assistant));
    }

    [Fact]
    public void HasInitialGreeting_RejectsNullTemplates()
    {
        var assistant = new AssistantConfig
        {
            DialingNumber = "10000",
            HelloMessageTempletes = null!
        };

        Assert.False(DefaultAudioProcessor.HasInitialGreeting(assistant));
    }

    [Fact]
    public async Task PromptPlaybackCompletion_CompletesOnlyOnce()
    {
        using SIPTransport transport = new();
        DeviceRegistrationRecord registration = new(
            "device-1",
            "sip:1001@device.test",
            "sip:1001@192.0.2.10:5060",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));
        using var device = new DeviceContext(
            TestServices.ScopeFactory,
            transport,
            registration,
            [new AssistantConfig { DialingNumber = "10000" }]);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        using var activeCall = new ActiveCallContext(
            device,
            "sip:1001@device.test",
            "10000",
            userAgent,
            mediaSession);

        Task<bool> firstPlayback = activeCall.BeginPromptPlayback();
        Task<bool> secondPlayback = activeCall.BeginPromptPlayback();
        activeCall.CompletePromptPlayback(fullyPlayed: true);

        Assert.True(await firstPlayback);
        Assert.False(await secondPlayback);
    }

    [Fact]
    public void UserAudioInputPause_DoesNotChangeAgentMediaPause()
    {
        using SIPTransport transport = new();
        DeviceRegistrationRecord registration = new(
            "device-1",
            "sip:1001@device.test",
            "sip:1001@192.0.2.10:5060",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));
        using var device = new DeviceContext(
            TestServices.ScopeFactory,
            transport,
            registration,
            [new AssistantConfig { DialingNumber = "10000" }]);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        using var activeCall = new ActiveCallContext(
            device,
            "sip:1001@device.test",
            "10000",
            userAgent,
            mediaSession);

        activeCall.PauseUserAudioInput();

        Assert.True(activeCall.IsUserAudioInputPaused);
        Assert.False(activeCall.IsAgentMediaPaused);

        activeCall.ResumeUserAudioInput();

        Assert.False(activeCall.IsUserAudioInputPaused);
    }
}
