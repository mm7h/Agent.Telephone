using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Sample.Server.MessageStore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class SqliteMessageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-telephone-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MessageStoreSeparatesIdentityAndPersistsAudioPathAndReadStateAsync()
    {
        const string userAor = "sip:private-user@example.test";
        SqliteMessageStore store = this.CreateStore();
        MessageRecord message = new()
        {
            Id = "message-one",
            TurnId = "call-one:1",
            UserAor = userAor,
            AssistantNumber = "10086",
            AudioPath = "D:\\offline-audio\\reply.mp3"
        };

        await store.SaveAsync(message);

        MessageRecord loaded = Assert.Single(await store.GetUnreadAsync(userAor, "10086"));
        Assert.Equal(message.AudioPath, loaded.AudioPath);
        Assert.Equal(1, loaded.Sequence);

        Assert.True(await store.MarkReadAsync(userAor, "10086", message.Id));
        Assert.Empty(await store.GetUnreadAsync(userAor, "10086"));
        Assert.Equal(DeliveryState.Read, (await store.GetAsync(userAor, "10086", message.Id))!.State);
    }

    [Fact]
    public async Task ConcurrentSavesUseUniquePersistedSequenceAsync()
    {
        SqliteMessageStore store = this.CreateStore();
        IEnumerable<Task<MessageRecord>> saves = Enumerable.Range(1, 20).Select(index =>
            store.SaveAsync(CreateMessage($"message-{index:D2}", DeliveryState.Unread, DateTimeOffset.UtcNow)));

        await Task.WhenAll(saves);

        IReadOnlyList<MessageRecord> unread = await store.GetUnreadAsync("sip:1001@example.test", "10086");
        Assert.Equal(Enumerable.Range(1, 20).Select(index => (long)index), unread.Select(message => message.Sequence));
        Assert.Equal(20, unread.Select(message => message.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task CleanupRemovesExpiredAndThenOldestReadMessagesAsync()
    {
        SqliteMessageStore store = this.CreateStore(maxMessages: 2);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await store.SaveAsync(CreateMessage("expired", DeliveryState.Unread, now.AddDays(-31)));
        await store.SaveAsync(CreateMessage("read", DeliveryState.Read, now.AddMinutes(-3)));
        await store.SaveAsync(CreateMessage("unread-1", DeliveryState.Unread, now.AddMinutes(-2)));
        await store.SaveAsync(CreateMessage("unread-2", DeliveryState.Unread, now.AddMinutes(-1)));

        MessageCleanupResult result = await store.CleanupAsync(now);

        Assert.Equal(1, result.ExpiredRemoved);
        Assert.Equal(1, result.OverflowRemoved);
        Assert.Null(await store.GetAsync("sip:1001@example.test", "10086", "expired"));
        Assert.Null(await store.GetAsync("sip:1001@example.test", "10086", "read"));
    }

    [Fact]
    public async Task LogicalDeletePolicyKeepsDeletedRowButExcludesItFromReadsAsync()
    {
        SqliteMessageStore store = this.CreateStore(useLogicalDelete: true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.SaveAsync(CreateMessage("expired", DeliveryState.Unread, now.AddDays(-31)));

        MessageCleanupResult result = await store.CleanupAsync(now);

        Assert.Equal(1, result.ExpiredRemoved);
        Assert.Null(await store.GetAsync("sip:1001@example.test", "10086", "expired"));
        await using SqliteConnection connection = new($"Data Source={Path.Combine(this._root, "messages.db")}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IsDelete FROM Messages WHERE Id = 'expired';";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExistingDatabaseIsUpgradedWithLogicalDeleteColumnAsync()
    {
        string databasePath = Path.Combine(this._root, "messages.db");
        Directory.CreateDirectory(this._root);
        await using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Messages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TurnId TEXT NOT NULL,
                    UserAor TEXT NOT NULL,
                    AssistantNumber TEXT NOT NULL,
                    AudioPath TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    State INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    ReadAt TEXT NULL,
                    UNIQUE(UserAor, AssistantNumber, Sequence)
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteMessageStore store = this.CreateStore();
        await store.SaveAsync(CreateMessage("migrated", DeliveryState.Unread, DateTimeOffset.UtcNow));

        Assert.NotNull(await store.GetAsync("sip:1001@example.test", "10086", "migrated"));
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

    private static MessageRecord CreateMessage(
        string id,
        DeliveryState state,
        DateTimeOffset createdAt) => new()
        {
            Id = id,
            TurnId = $"turn-{id}",
            UserAor = "sip:1001@example.test",
            AssistantNumber = "10086",
            AudioPath = $"D:\\offline-audio\\{id}.mp3",
            State = state,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
}
