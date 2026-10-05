using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Sample.Server.MessageStore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class SqliteMessageStoreTests : IDisposable
{
    private const string UserAor = "sip:1001@example.test";
    private const string AssistantNumber = "10086";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-telephone-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoresFullMessagesAndOrderedSegmentsAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConversationMessage user = CreateMessage("user-1", "turn-1", ConversationRole.User, DeliveryState.Read, "用户的问题", now);
        ConversationMessage assistant = CreateMessage("assistant-1", "turn-1", ConversationRole.Assistant, DeliveryState.Generating, string.Empty, now);

        await store.SaveConversationMessageAsync(user);
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = user.Id, Sequence = 1, TextContent = user.FullText, CreatedAt = now });
        await store.SaveConversationMessageAsync(assistant);
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = assistant.Id, Sequence = 2, TextContent = "第二句", AudioPath = "reply-2.wav", CreatedAt = now });
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = assistant.Id, Sequence = 1, TextContent = "第一句", CreatedAt = now });
        await store.FinalizeAssistantMessageAsync(UserAor, AssistantNumber, assistant.Id, "第一句第二句", DeliveryState.Unread);

        ConversationMessage loaded = Assert.IsType<ConversationMessage>(await store.GetAssistantMessageAsync(UserAor, AssistantNumber, assistant.Id));
        Assert.Equal(DeliveryState.Unread, loaded.State);
        Assert.Equal("第一句第二句", loaded.FullText);
        Assert.Equal([1L, 2L], (await store.GetMessageSegmentsAsync(assistant.Id)).Select(segment => segment.Sequence));

        ConversationMessage unread = Assert.Single(await store.GetUnreadAssistantMessagesAsync(UserAor, AssistantNumber));
        Assert.Equal(assistant.Id, unread.Id);
        await store.MarkAssistantMessageReadAsync(UserAor, AssistantNumber, assistant.Id);
        Assert.Empty(await store.GetUnreadAssistantMessagesAsync(UserAor, AssistantNumber));
        Assert.Equal(DeliveryState.Read, (await store.GetAssistantMessageAsync(UserAor, AssistantNumber, assistant.Id))!.State);
    }

    [Fact]
    public async Task KeepsUnreadMessagesSeparatedByAssistantAndExcludesFailedTurnsAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConversationMessage unread = CreateMessage("unread", "turn-1", ConversationRole.Assistant, DeliveryState.Generating, string.Empty, now);
        ConversationMessage failed = CreateMessage("failed", "turn-2", ConversationRole.Assistant, DeliveryState.Generating, string.Empty, now.AddSeconds(1));
        ConversationMessage otherAssistant = CreateMessage("other", "turn-3", ConversationRole.Assistant, DeliveryState.Generating, string.Empty, now.AddSeconds(2)) with { AssistantNumber = "10010" };

        await store.SaveConversationMessageAsync(unread);
        await store.SaveConversationMessageAsync(failed);
        await store.SaveConversationMessageAsync(otherAssistant);
        await store.FinalizeAssistantMessageAsync(UserAor, AssistantNumber, unread.Id, "可播放", DeliveryState.Unread);
        await store.FailAssistantMessageAsync(UserAor, AssistantNumber, failed.Id, "失败的回复");
        await store.FinalizeAssistantMessageAsync(UserAor, otherAssistant.AssistantNumber, otherAssistant.Id, "另一助手", DeliveryState.Unread);

        ConversationMessage result = Assert.Single(await store.GetUnreadAssistantMessagesAsync(UserAor, AssistantNumber));
        Assert.Equal(unread.Id, result.Id);
    }

    [Fact]
    public async Task GetsConversationMessagesInOrderForTheCurrentUserAndAssistantAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConversationMessage first = CreateMessage("first", "turn-1", ConversationRole.User, DeliveryState.Read, "第一句", now);
        ConversationMessage second = CreateMessage("second", "turn-1", ConversationRole.Assistant, DeliveryState.Read, "第二句", now.AddSeconds(1));
        ConversationMessage otherAssistant = CreateMessage("other-assistant", "turn-2", ConversationRole.User, DeliveryState.Read, "不应读取", now.AddSeconds(2)) with { AssistantNumber = "10010" };
        ConversationMessage otherUser = CreateMessage("other-user", "turn-3", ConversationRole.User, DeliveryState.Read, "不应读取", now.AddSeconds(3)) with { UserAor = "sip:1002@example.test" };

        await store.SaveConversationMessageAsync(second);
        await store.SaveConversationMessageAsync(otherAssistant);
        await store.SaveConversationMessageAsync(otherUser);
        await store.SaveConversationMessageAsync(first);

        IReadOnlyList<ConversationMessage> messages = await store.GetConversationMessagesAsync(UserAor, AssistantNumber);

        Assert.Equal([first.Id, second.Id], messages.Select(message => message.Id));
        Assert.Equal([ConversationRole.User, ConversationRole.Assistant], messages.Select(message => message.Role));
    }

    [Fact]
    public async Task DiscardRemovesOnlyTheAssistantReplyForTheTurnAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConversationMessage user = CreateMessage("user-1", "turn-1", ConversationRole.User, DeliveryState.Read, "用户的问题", now);
        ConversationMessage assistant = CreateMessage("assistant-1", "turn-1", ConversationRole.Assistant, DeliveryState.Generating, string.Empty, now);

        await store.SaveConversationMessageAsync(user);
        await store.SaveConversationMessageAsync(assistant);
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = assistant.Id, Sequence = 1, TextContent = "会被丢弃" });
        await store.DiscardAssistantMessageAsync(UserAor, AssistantNumber, assistant.Id);

        Assert.Null(await store.GetAssistantMessageAsync(UserAor, AssistantNumber, assistant.Id));
        Assert.Empty(await store.GetMessageSegmentsAsync(assistant.Id));
        await store.SaveMessageSegmentAsync(new MessageSegment { MessageId = user.Id, Sequence = 1, TextContent = user.FullText });
    }

    [Fact]
    public async Task CleanupRemovesExpiredMessagesAndPreservesDeviceRegistrationsAsync()
    {
        SqliteMessageStore store = this.CreateStore(maxMessages: 1, useLogicalDelete: true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConversationMessage expired = CreateMessage("expired", "turn-expired", ConversationRole.Assistant, DeliveryState.Unread, "过期", now.AddDays(-31));
        ConversationMessage retained = CreateMessage("retained", "turn-retained", ConversationRole.Assistant, DeliveryState.Unread, "保留", now.AddMinutes(-1));

        await store.SaveConversationMessageAsync(expired);
        await store.SaveConversationMessageAsync(retained);
        MessageCleanupResult cleanup = await store.CleanupConversationMessagesAsync(now);

        Assert.Equal(1, cleanup.ExpiredRemoved);
        Assert.Null(await store.GetAssistantMessageAsync(UserAor, AssistantNumber, expired.Id));

        DeviceRegistrationRecord registration = new(
            "sip-device:v1:default:1001%40device.test",
            UserAor,
            "sip:1001@192.0.2.10:5060",
            now,
            now,
            now.AddHours(1));
        await store.SaveDeviceRegistrationAsync(registration);
        Assert.Equal(registration, Assert.Single(await store.GetActiveDeviceRegistrationsAsync(now)));
    }

    [Fact]
    public async Task ExistingDeviceRegistrationsTableReceivesLogicalDeleteColumnAsync()
    {
        string databasePath = Path.Combine(this._root, "messages.db");
        Directory.CreateDirectory(this._root);
        await using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE DeviceRegistrations (
                    DeviceId TEXT NOT NULL PRIMARY KEY,
                    Aor TEXT NOT NULL,
                    Contact TEXT NOT NULL,
                    RegisteredAt TEXT NOT NULL,
                    RefreshedAt TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteMessageStore store = this.CreateStore(useLogicalDelete: true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.SaveDeviceRegistrationAsync(new DeviceRegistrationRecord(
            "sip-device:v1:default:1001%40device.test",
            UserAor,
            "sip:1001@192.0.2.10:5060",
            now,
            now,
            now.AddHours(1)));

        Assert.Single(await store.GetActiveDeviceRegistrationsAsync(now));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, true);
        }
    }

    private SqliteMessageStore CreateStore(int maxMessages = 100, bool useLogicalDelete = false) => new(new SqliteMessageStoreOptions
    {
        DatabasePath = Path.Combine(this._root, "messages.db"),
        RetentionDays = 30,
        MaxMessagesPerConversation = maxMessages,
        UseLogicalDelete = useLogicalDelete,
    });

    private static ConversationMessage CreateMessage(
        string id,
        string turnId,
        ConversationRole role,
        DeliveryState state,
        string fullText,
        DateTimeOffset createdAt) => new()
        {
            Id = id,
            TurnId = turnId,
            UserAor = UserAor,
            AssistantNumber = AssistantNumber,
            Role = role,
            FullText = fullText,
            State = state,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ReadAt = state == DeliveryState.Read ? createdAt : null,
        };
}
