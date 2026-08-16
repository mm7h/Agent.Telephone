using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Media.Abstractions.Dtos;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.OfflineDialogue;
using Agent.Telephone.Sample.Server.MessageStore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class OfflineDialogueProviderTests : IDisposable
{
    private const string DeviceNumber = "1001";
    private const string AssistantNumber = "10086";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TurnQueuesUserTextAssistantSegmentsAndUnreadCompletionAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "用户的问题", CancellationToken.None);
        await provider.BeginOfflinePersistenceAsync(turn, CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "第一句。", null, CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "第二句。", null, CancellationToken.None);
        await provider.CompleteTurnAsync(turn, read: false, CancellationToken.None);
        await provider.FlushTurnAsync(turn, CancellationToken.None);

        ConversationMessage assistant = Assert.Single(await store.GetUnreadAssistantMessagesAsync(session.Call.UserAor, AssistantNumber));
        Assert.Equal(turn.AssistantMessageId, assistant.Id);
        Assert.Equal("第一句。第二句。", assistant.FullText);
        Assert.Equal(["第一句。", "第二句。"], (await store.GetMessageSegmentsAsync(assistant.Id)).Select(segment => segment.TextContent));

        await using SqliteConnection connection = new($"Data Source={Path.Combine(this._root, "messages.db")}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ConversationMessages WHERE TurnId = $turnId;";
        command.Parameters.AddWithValue("$turnId", turn.TurnId);
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CancelledTurnDiscardsAssistantTextAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "用户的问题", CancellationToken.None);
        await provider.BeginOfflinePersistenceAsync(turn, CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "不应保留", null, CancellationToken.None);
        await provider.DiscardTurnAsync(turn, CancellationToken.None);
        await provider.FlushTurnAsync(turn, CancellationToken.None);

        Assert.Empty(await store.GetUnreadAssistantMessagesAsync(session.Call.UserAor, AssistantNumber));
        Assert.Null(await store.GetAssistantMessageAsync(session.Call.UserAor, AssistantNumber, turn.AssistantMessageId));
    }

    [Fact]
    public async Task OnlineTurnStaysInMemoryUntilTheCallEndsAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "在线问题", CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "在线回答", null, CancellationToken.None);
        await provider.CompleteTurnAsync(turn, read: true, CancellationToken.None);
        await provider.FlushTurnAsync(turn, CancellationToken.None);

        Assert.Empty(await store.GetConversationMessagesAsync(session.Call.UserAor, AssistantNumber));
    }

    [Fact]
    public async Task OfflineTransitionPersistsBufferedAndLaterSegmentsInOrderAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "用户的问题", CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "已播放。", null, CancellationToken.None);
        await provider.BeginOfflinePersistenceAsync(turn, CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "后续生成。", null, CancellationToken.None);
        await provider.CompleteTurnAsync(turn, read: false, CancellationToken.None);
        await provider.FlushTurnAsync(turn, CancellationToken.None);

        ConversationMessage assistant = Assert.Single(await store.GetUnreadAssistantMessagesAsync(session.Call.UserAor, AssistantNumber));
        Assert.Equal("已播放。后续生成。", assistant.FullText);
        Assert.Equal(["已播放。", "后续生成。"], (await store.GetMessageSegmentsAsync(assistant.Id)).Select(segment => segment.TextContent));
    }

    [Fact]
    public async Task CompletedOnlineTurnsArePersistedAsReadOnCallCloseAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "在线问题", CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "在线回答。", null, CancellationToken.None);
        await provider.CompleteTurnAsync(turn, read: true, CancellationToken.None);
        session.Call.AIAgentContext.RecordCompletedOnlineTurn(turn);

        await provider.PersistCompletedOnlineTurnsAsync(session.Call, CancellationToken.None);

        ConversationMessage? assistant = await store.GetAssistantMessageAsync(session.Call.UserAor, AssistantNumber, turn.AssistantMessageId);
        Assert.NotNull(assistant);
        Assert.Equal(DeliveryState.Read, assistant.State);
        Assert.Empty(await store.GetUnreadAssistantMessagesAsync(session.Call.UserAor, AssistantNumber));
        Assert.Equal(["在线回答。"], (await store.GetMessageSegmentsAsync(turn.AssistantMessageId)).Select(segment => segment.TextContent));
    }

    [Fact]
    public async Task CallIsBusyUntilNormalHistoryPersistenceCompletesAsync()
    {
        using TestCallSession session = CreateActiveCall();
        var store = new BlockingTelephoneStore();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "在线问题", CancellationToken.None);
        await provider.AppendAssistantSegmentAsync(turn, "在线回答。", null, CancellationToken.None);
        await provider.CompleteTurnAsync(turn, read: true, CancellationToken.None);
        session.Call.AIAgentContext.RecordCompletedOnlineTurn(turn);
        session.Call.AIAgentContext.PrivateProvider.SetOfflineDialogue(provider);

        session.Device.CloseCallSession(session.Call);

        Assert.True(session.Device.IsCallOccupied);
        SIPRequest redial = SIPRequest.GetRequest(SIPMethodsEnum.INVITE, SIPURI.ParseSIPURI($"sip:{AssistantNumber}@server.test"));
        Assert.False(session.Device.TryInitializeCallSession(redial, out ActiveCallContext? _));

        store.AllowWrites();
        Assert.True(await WaitUntilAsync(() => Task.FromResult(!session.Device.IsCallOccupied)));
    }

    [Fact]
    public async Task ProactiveCallbackSendsInviteToRegisteredContactAsync()
    {
        using SIPTransport callbackTransport = new();
        using SIPTransport phoneTransport = new();
        var callbackChannel = new SIPUDPChannel(System.Net.IPAddress.Loopback, 0);
        var phoneChannel = new SIPUDPChannel(System.Net.IPAddress.Loopback, 0);
        callbackTransport.AddSIPChannel(callbackChannel);
        phoneTransport.AddSIPChannel(phoneChannel);

        TaskCompletionSource<SIPRequest> inviteReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        phoneTransport.SIPTransportRequestReceived += OnRequestReceivedAsync;
        try
        {
            string contactUri = $"sip:{DeviceNumber}@{phoneChannel.ListeningSIPEndPoint.GetIPEndPoint()}";
            SIPRequest register = CreateRegisterRequest();
            using var device = new DeviceContext(
                callbackTransport,
                register,
                SIPURI.ParseSIPURI(contactUri),
                300,
                [new AssistantConfig { DialingNumber = AssistantNumber }]);
            using var provider = new DefaultOfflineDialogue(
                new BlockingTelephoneStore(),
                callbackTransport,
                null!,
                null!,
                null!,
                new TelephoneConfig { SIPConfig = new SIPConfig { CallbackTimeoutSeconds = 5 } },
                NullLogger<DefaultOfflineDialogue>.Instance);
            var turn = new OfflineDialogueTurn(
                "callback-turn",
                $"sip:{DeviceNumber}@device.test",
                AssistantNumber,
                "callback-user-message",
                "callback-message",
                "用户的问题");

            await provider.StartProactiveCallAsync(device, turn);

            SIPRequest invite = await inviteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SIPMethodsEnum.INVITE, invite.Method);
            Assert.Equal(DeviceNumber, invite.URI.User);
            Assert.Equal(AssistantNumber, invite.Header.From.FromURI.User);

            await provider.StopProactiveCallAsync(turn);
            await provider.WaitForProactiveCallResultAsync(turn, CancellationToken.None);
        }
        finally
        {
            phoneTransport.SIPTransportRequestReceived -= OnRequestReceivedAsync;
            callbackTransport.Shutdown();
            phoneTransport.Shutdown();
        }

        Task OnRequestReceivedAsync(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
        {
            if (request.Method == SIPMethodsEnum.INVITE)
            {
                inviteReceived.TrySetResult(request);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CallbackPlaybackWaitsForGeneratingMessageAndPlaysEachCommittedSegmentAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string messageId = "assistant-message";
        var played = new List<string>();

        await store.SaveConversationMessageAsync(new ConversationMessage
        {
            Id = messageId,
            TurnId = "turn-1",
            UserAor = session.Call.UserAor,
            AssistantNumber = AssistantNumber,
            Role = ConversationRole.Assistant,
            State = DeliveryState.Generating,
            CreatedAt = now,
            UpdatedAt = now,
        });

        Task playback = provider.PlayAssistantMessageAsync(
            session.Call,
            messageId,
            (text, _, _, _) =>
            {
                played.Add(text);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        await Task.Delay(150);
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = messageId, Sequence = 1, TextContent = "第一句" });
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = messageId, Sequence = 2, TextContent = "第二句" });
        await store.FinalizeAssistantMessageAsync(session.Call.UserAor, AssistantNumber, messageId, "第一句第二句", DeliveryState.Unread);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(played.Count == 1)));
        Assert.Equal(["第一句"], played);
        session.Call.CompletePromptPlayback(fullyPlayed: true);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(played.Count == 2)));
        session.Call.CompletePromptPlayback(fullyPlayed: true);
        await playback.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["第一句", "第二句"], played);
        Assert.Equal(DeliveryState.Read, (await store.GetAssistantMessageAsync(session.Call.UserAor, AssistantNumber, messageId))!.State);
    }

    [Fact]
    public async Task PersistenceQueueDoesNotWaitForSlowStoreWritesAsync()
    {
        var store = new BlockingTelephoneStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        Assert.True(provider.Build(ModelSetting.Empty));

        OfflineDialogueTurn turn = await provider.BeginTurnAsync(session.Call, 1, "用户的问题", CancellationToken.None);
        await provider.BeginOfflinePersistenceAsync(turn, CancellationToken.None);
        Task append = provider.AppendAssistantSegmentAsync(turn, "回复", null, CancellationToken.None).AsTask();

        Assert.Same(append, await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(1))));
        await provider.CompleteTurnAsync(turn, read: false, CancellationToken.None);
        store.AllowWrites();
        await provider.FlushTurnAsync(turn, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        MessageSegment assistantSegment = Assert.Single(store.Segments, segment => segment.MessageId == turn.AssistantMessageId);
        Assert.Equal("回复", assistantSegment.TextContent);
    }

    [Fact]
    public async Task InitialCallMenuPlaysUnreadTextSegmentsBeforeResumingConversationAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        var audioProcessor = new MenuAudioProcessor();
        session.Call.AIAgentContext.PrivateProvider.SetAudioProcessor(audioProcessor);
        session.Call.AIAgentContext.PrivateProvider.SetDtmfInput(new SelectedDtmfInput(DtmfKey.One));
        const string messageId = "unread-message";
        var played = new List<string>();
        var playbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await store.SaveConversationMessageAsync(new ConversationMessage
        {
            Id = messageId,
            TurnId = "previous-turn",
            UserAor = session.Call.UserAor,
            AssistantNumber = AssistantNumber,
            Role = ConversationRole.Assistant,
            FullText = "第一句第二句",
            State = DeliveryState.Unread,
        });
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = messageId, Sequence = 1, TextContent = "第一句" });
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = messageId, Sequence = 2, TextContent = "第二句" });

        await provider.StartInitialCallFlowAsync(
            session.Call,
            (text, _, _, _) =>
            {
                played.Add(text);
                session.Call.CompletePromptPlayback(fullyPlayed: true);
                if (played.Count == 2)
                {
                    playbackCompleted.TrySetResult();
                }

                return Task.FromResult(true);
            },
            CancellationToken.None);

        await playbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await WaitUntilAsync(
            async () => (await store.GetAssistantMessageAsync(session.Call.UserAor, AssistantNumber, messageId))!.State == DeliveryState.Read));
        Assert.Equal(["第一句", "第二句"], played);
        Assert.Equal(DefaultOfflineDialogue.GetMenuAudioFiles(1), Assert.Single(audioProcessor.Prompts));
        Assert.False(session.Call.IsUserAudioInputPaused);
    }

    [Fact]
    public async Task InitialGreetingWaitsForUnreadMessagePlaybackAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        var audioProcessor = new MenuAudioProcessor(beginInitialGreeting: true);
        session.Call.AIAgentContext.PrivateProvider.SetAudioProcessor(audioProcessor);
        session.Call.AIAgentContext.PrivateProvider.SetDtmfInput(new SelectedDtmfInput(DtmfKey.One));
        const string messageId = "unread-message";
        var played = new List<string>();

        await this.CreateUnreadAssistantMessageAsync(store, session.Call, messageId, "离线留言");
        await provider.StartInitialCallFlowAsync(
            session.Call,
            (text, _, _, _) =>
            {
                played.Add(text);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(played.Count == 1)));
        Assert.False(audioProcessor.InitialGreetingStarted.Task.IsCompleted);

        session.Call.CompletePromptPlayback(fullyPlayed: true);

        await audioProcessor.InitialGreetingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InitialCallMenuKeyTwoMarksOnlyTheUnreadSnapshotAsReadAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        using TestCallSession session = CreateActiveCall();
        using DefaultOfflineDialogue provider = this.CreateProvider(store);
        session.Call.AIAgentContext.PrivateProvider.SetAudioProcessor(new MenuAudioProcessor());
        session.Call.AIAgentContext.PrivateProvider.SetDtmfInput(new SelectedDtmfInput(DtmfKey.Two));
        const string firstMessageId = "unread-first";
        const string secondMessageId = "unread-second";

        await this.CreateUnreadAssistantMessageAsync(store, session.Call, firstMessageId, "第一条");
        await this.CreateUnreadAssistantMessageAsync(store, session.Call, secondMessageId, "第二条");
        await provider.StartInitialCallFlowAsync(
            session.Call,
            static (_, _, _, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () =>
            (await store.GetAssistantMessageAsync(session.Call.UserAor, AssistantNumber, firstMessageId))!.State == DeliveryState.Read &&
            (await store.GetAssistantMessageAsync(session.Call.UserAor, AssistantNumber, secondMessageId))!.State == DeliveryState.Read));
    }

    [Theory]
    [InlineData(1, "prompt/offline_message_less_part_1.mp3", "numbers/1.mp3", "prompt/offline_message_less_part_2.mp3")]
    [InlineData(9, "prompt/offline_message_less_part_1.mp3", "numbers/9.mp3", "prompt/offline_message_less_part_2.mp3")]
    [InlineData(10, "prompt/offline_message_many.mp3")]
    public void GetMenuAudioFilesUsesCountPromptUntilTen(int messageCount, params string[] expectedPaths)
    {
        Assert.Equal(expectedPaths, DefaultOfflineDialogue.GetMenuAudioFiles(messageCount));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, true);
        }
    }

    private SqliteMessageStore CreateStore() => new(new SqliteMessageStoreOptions
    {
        DatabasePath = Path.Combine(this._root, "messages.db"),
    });

    private DefaultOfflineDialogue CreateProvider(ITelephoneStore store) => new(
        store,
        null!,
        null!,
        null!,
        null!,
        new TelephoneConfig(),
        NullLogger<DefaultOfflineDialogue>.Instance);

    private async Task CreateUnreadAssistantMessageAsync(
        SqliteMessageStore store,
        ActiveCallContext activeCall,
        string messageId,
        string text)
    {
        await store.SaveConversationMessageAsync(new ConversationMessage
        {
            Id = messageId,
            TurnId = $"turn-{messageId}",
            UserAor = activeCall.UserAor,
            AssistantNumber = AssistantNumber,
            Role = ConversationRole.Assistant,
            FullText = text,
            State = DeliveryState.Unread,
        });
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = messageId, Sequence = 1, TextContent = text });
    }

    private static TestCallSession CreateActiveCall()
    {
        SIPTransport transport = new();
        SIPRequest register = CreateRegisterRequest();
        DeviceContext device = new(
            transport,
            register,
            SIPURI.ParseSIPURI($"sip:{DeviceNumber}@192.0.2.10:5060"),
            300,
            [new AssistantConfig { DialingNumber = AssistantNumber }]);
        Assert.True(device.TryBeginCallback());
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        Assert.True(device.TryAttachCallbackCallSession(
            $"sip:{DeviceNumber}@device.test",
            AssistantNumber,
            userAgent,
            mediaSession,
            out ActiveCallContext? activeCall));
        Assert.NotNull(activeCall);
        device.EndCallback();
        return new TestCallSession(transport, device, activeCall);
    }

    private static SIPRequest CreateRegisterRequest()
    {
        SIPRequest request = SIPRequest.GetRequest(SIPMethodsEnum.REGISTER, SIPURI.ParseSIPURI("sip:registrar@127.0.0.1"));
        request.Header.From = new SIPFromHeader(null, SIPURI.ParseSIPURI($"sip:{DeviceNumber}@device.test"), CallProperties.CreateNewTag());
        request.Header.To = new SIPToHeader(null, SIPURI.ParseSIPURI("sip:registrar@server.test"), null);
        request.Header.Contact = [new SIPContactHeader(null, SIPURI.ParseSIPURI($"sip:{DeviceNumber}@192.0.2.10:5060")) { Expires = 300 }];
        request.Header.Expires = 300;
        return request;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
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
        public DeviceContext Device => this._device;

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

    private sealed class BlockingTelephoneStore : ITelephoneStore
    {
        private readonly TaskCompletionSource _writesAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<MessageSegment> Segments { get; } = [];

        public void AllowWrites()
        {
            this._writesAllowed.TrySetResult();
        }

        public async Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
        {
            await this._writesAllowed.Task.WaitAsync(cancellationToken);
        }

        public async Task SaveMessageSegmentAsync(MessageSegment segment, CancellationToken cancellationToken = default)
        {
            await this._writesAllowed.Task.WaitAsync(cancellationToken);
            this.Segments.Add(segment);
        }

        public async Task FinalizeAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string fullText,
            DeliveryState state,
            CancellationToken cancellationToken = default)
        {
            await this._writesAllowed.Task.WaitAsync(cancellationToken);
        }

        public Task FailAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string fullText,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DiscardAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationMessage>> GetUnreadAssistantMessagesAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>([]);

        public Task<IReadOnlyList<ConversationMessage>> GetConversationMessagesAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>([]);

        public Task<ConversationMessage?> GetAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationMessage?>(null);

        public Task<IReadOnlyList<MessageSegment>> GetMessageSegmentsAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MessageSegment>>([]);

        public Task MarkAssistantMessageReadAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkAssistantMessagesReadAsync(
            string userAor,
            string assistantNumber,
            IReadOnlyCollection<string> messageIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MessageCleanupResult> CleanupConversationMessagesAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(new MessageCleanupResult(0, 0));

        public Task SaveDeviceRegistrationAsync(DeviceRegistrationRecord registration, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveDeviceRegistrationAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DeviceRegistrationRecord>> GetActiveDeviceRegistrationsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceRegistrationRecord>>([]);
    }

    private sealed class MenuAudioProcessor : BaseProvider<MenuAudioProcessor, ModelSetting>, IAudioProcessor
    {
        private readonly bool _beginInitialGreeting;

        public MenuAudioProcessor(bool beginInitialGreeting = false)
            : base(NullLogger<MenuAudioProcessor>.Instance)
        {
            this._beginInitialGreeting = beginInitialGreeting;
        }

        public override string ProviderType => "audio-processor";
        public override string ModelName => nameof(MenuAudioProcessor);
        public List<IReadOnlyList<string>> Prompts { get; } = [];
        public TaskCompletionSource InitialGreetingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<float[], bool, bool, string?>? OnMixedAudioDataAvailable
        {
            add { }
            remove { }
        }

        public override bool Build(ModelSetting settings) => true;
        public Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token) => Task.FromResult(Array.Empty<float>());
        public Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token) => Task.FromResult(Array.Empty<byte>());
        public bool InitializeMixer(int outputSampleRate, int outputChannels, int frameDuration) => true;
        public bool TryBeginInitialGreeting(ActiveCallContext activeCall, bool isInbound) => this._beginInitialGreeting;
        public void StartInitialGreeting(ActiveCallContext activeCall, Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt) =>
            this.InitialGreetingStarted.TrySetResult();

        public Task<bool> PlayCachedPromptAsync(
            ActiveCallContext activeCall,
            IReadOnlyList<string> relativeFilePaths,
            CancellationToken cancellationToken)
        {
            this.Prompts.Add(relativeFilePaths.ToArray());
            return Task.FromResult(true);
        }

        public Task<bool> PlayFilePromptAsync(ActiveCallContext activeCall, string filePath, CancellationToken cancellationToken) => Task.FromResult(true);
        public void ProcessAudio(AudioType audioType, float[] audioData, string? sentenceId) { }
        public void CompleteStream(AudioType audioType) { }
        public void ClearAllBuffers() { }
        public void RegisterSubtitle(string sentenceId, AudioType audioType, TtsStatus ttsStatus, string text) { }

        public bool GetSubtitle(string sentenceId, out AudioSubtitle subtitle)
        {
            subtitle = default;
            return false;
        }

        public override void Dispose() { }
    }

    private sealed class SelectedDtmfInput : BaseProvider<SelectedDtmfInput, ModelSetting>, IDtmfInput
    {
        private readonly DtmfKey _key;

        public SelectedDtmfInput(DtmfKey key)
            : base(NullLogger<SelectedDtmfInput>.Instance)
        {
            this._key = key;
        }

        public override string ProviderType => "dtmf-input";
        public override string ModelName => nameof(SelectedDtmfInput);
        public override bool Build(ModelSetting settings) => true;
        public Task<DtmfInputResult> RequestDtmfInputAsync(
            ActiveCallContext call,
            DtmfKey keys,
            CancellationToken cancellationToken) => Task.FromResult(
                new DtmfInputResult(DtmfInputStatus.Accepted) { SelectedKey = this._key });

        public Task<DtmfKey?> WaitForDtmfKeyAsync(
            ActiveCallContext call,
            DtmfKey keys,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult<DtmfKey?>(this._key);

        public void HandleDtmfTone(ActiveCallContext call, byte tone) { }
        public override void Dispose() { }
    }
}
