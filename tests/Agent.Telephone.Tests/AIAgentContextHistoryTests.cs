using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.AI;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AIAgentContextHistoryTests
{
    [Fact]
    public void LoadsPersistedMessagesAsOneSystemMessageInConversationOrder()
    {
        using SIPTransport transport = new();
        DeviceContext device = new(
            TestServices.ScopeFactory,
            transport,
            this.CreateRegisterRequest(),
            SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060"),
            300,
            [new AssistantConfig { DialingNumber = "10086" }]);
        Assert.True(device.TryBeginCallback());

        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        Assert.True(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            "10086",
            userAgent,
            mediaSession,
            out ActiveCallContext? activeCall));
        Assert.NotNull(activeCall);

        try
        {
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            activeCall.AIAgentContext.LoadPersistedChatHistory(
            [
                new ConversationMessage
                {
                    Id = "assistant",
                    Role = ConversationRole.Assistant,
                    FullText = "此前回答",
                    CreatedAt = timestamp,
                },
                new ConversationMessage
                {
                    Id = "user",
                    Role = ConversationRole.User,
                    FullText = "此前问题",
                    CreatedAt = timestamp,
                },
            ]);

            ChatMessage history = Assert.Single(activeCall.AIAgentContext.ChatHistory);
            Assert.Equal(ChatRole.System, history.Role);
            Assert.Contains("用户：此前问题", history.Text);
            Assert.Contains("助手：此前回答", history.Text);
            Assert.True(history.Text!.IndexOf("用户：此前问题", StringComparison.Ordinal) < history.Text.IndexOf("助手：此前回答", StringComparison.Ordinal));
        }
        finally
        {
            device.CloseCallSession(activeCall);
            device.EndCallback();
        }
    }

    private SIPRequest CreateRegisterRequest()
    {
        SIPRequest request = SIPRequest.GetRequest(SIPMethodsEnum.REGISTER, SIPURI.ParseSIPURI("sip:registrar@127.0.0.1"));
        request.Header.From = new SIPFromHeader(null, SIPURI.ParseSIPURI("sip:1001@device.test"), CallProperties.CreateNewTag());
        request.Header.To = new SIPToHeader(null, SIPURI.ParseSIPURI("sip:1001@server.test"), null);
        request.Header.Contact = [new SIPContactHeader(null, SIPURI.ParseSIPURI("sip:1001@192.0.2.10:5060")) { Expires = 300 }];
        request.Header.Expires = 300;
        return request;
    }
}
