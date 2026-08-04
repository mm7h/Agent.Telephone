using System.Buffers.Binary;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.Conversation.Persistence;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class FilePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-telephone-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigDefaultsMatchRetentionAndTimeoutPolicy()
    {
        var telephone = new TelephoneConfig();

        Assert.Equal(30, new SIPConfig().AgentInitializationTimeoutSeconds);
        Assert.Equal(30, new SIPConfig().CallbackTimeoutSeconds);
        Assert.Equal("./data/messages", telephone.MessageStoreConfig.RootPath);
        Assert.Equal(30, telephone.MessageStoreConfig.RetentionDays);
        Assert.Equal(100, telephone.MessageStoreConfig.MaxMessagesPerConversation);
        Assert.Equal(20, telephone.MessageStoreConfig.RecentConversationTurns);
        Assert.Empty(new AssistantConfig().AllowedTools);
    }

    [Fact]
    public void PcmWaveHelperCreatesMono16BitLittleEndianWave()
    {
        short[] samples = [-32768, 0, 32767];

        byte[] wave = PcmWaveHelper.CreateMono16BitWave(samples, 8_000);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4, 4)));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22, 2)));
        Assert.Equal(8_000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34, 2)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40, 4)));
        short[] decoded = [
            BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(46, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(48, 2))
        ];
        Assert.Equal(samples, decoded);
    }

    [Fact]
    public async Task ConversationStoreKeepsMostRecentConfiguredTurnsAsync()
    {
        var store = new FileConversationStore(this.CreateConfig(recentTurns: 2));
        var first = CreateConversation("first", DateTimeOffset.UtcNow.AddMinutes(-2));
        var second = CreateConversation("second", DateTimeOffset.UtcNow.AddMinutes(-1));
        var third = CreateConversation("third", DateTimeOffset.UtcNow);

        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.SaveAsync(third);

        var recent = await store.GetRecentAsync(first.UserAor, first.AssistantNumber);

        Assert.Equal(["second", "third"], recent.Select(item => item.Id));
        Assert.Equal(2, Directory.EnumerateFiles(this._root, "*.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task AssistantMemoryConfigurationOverridesConversationRetentionAsync()
    {
        var config = new TelephoneConfig
        {
            MessageStoreConfig = this.CreateConfig(recentTurns: 1),
            AssistantConfigs =
            [
                new AssistantConfig
                {
                    DialingNumber = "10086",
                    Memory = "LongMemory"
                }
            ],
            ModelConfig = new ModelConfig
            {
                ConfiguredSettings = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
                {
                    ["Memory"] = new()
                    {
                        ["LongMemory"] = new()
                        {
                            ["MaximumTurns"] = "3"
                        }
                    }
                }
            }
        };
        var store = new FileConversationStore(config);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await store.SaveAsync(CreateConversation("first", now.AddMinutes(-2)));
        await store.SaveAsync(CreateConversation("second", now.AddMinutes(-1)));
        await store.SaveAsync(CreateConversation("third", now));

        IReadOnlyList<ConversationTurn> recent = await store.GetRecentAsync(
            "sip:1001@example.test",
            "10086",
            maximumTurns: 3);

        Assert.Equal(["first", "second", "third"], recent.Select(item => item.Id));
    }

    [Fact]
    public async Task MessageStoreSeparatesIdentityAndPersistsTextWaveAndReadStateAsync()
    {
        const string UserAor = "sip:private-user@example.test";
        var store = new FileMessageStore(this.CreateConfig());
        var message = new MessageRecord
        {
            Id = "message-one",
            TurnId = "turn-one",
            UserAor = UserAor,
            AssistantNumber = "10086",
            Text = "完整回复",
            State = DeliveryState.Unread
        };

        await store.SaveAsync(message, new byte[] { 1, 2, 3, 4 });

        var files = Directory.EnumerateFiles(this._root, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(3, files.Length);
        Assert.DoesNotContain(files, path => path.Contains(UserAor, StringComparison.OrdinalIgnoreCase));
        var loaded = Assert.Single(await store.GetUnreadAsync(UserAor, "10086"));
        Assert.Equal("完整回复", loaded.Text);
        Assert.NotNull(loaded.WavePath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(loaded.WavePath!));

        Assert.True(await store.MarkReadAsync(UserAor, "10086", message.Id));
        Assert.Empty(await store.GetUnreadAsync(UserAor, "10086"));
        Assert.Equal(DeliveryState.Read, (await store.GetAsync(UserAor, "10086", message.Id))!.State);
    }

    [Fact]
    public async Task RecoveryCreatesOneUnreadMessageAndInterruptsRunningTurnAsync()
    {
        var config = this.CreateConfig();
        var turnStore = new FileTurnStore(config);
        var messageStore = new FileMessageStore(config);
        var recovery = new FileInterruptedTurnRecovery(turnStore, messageStore);
        var running = new TurnRecord
        {
            Id = "running-turn",
            UserAor = "sip:1001@example.test",
            AssistantNumber = "10086",
            UserText = "执行耗时任务",
            State = TurnState.Running
        };
        await turnStore.SaveAsync(running);

        Assert.Equal(1, await recovery.RecoverAsync("任务因服务重启而中断"));
        Assert.Equal(0, await recovery.RecoverAsync("任务因服务重启而中断"));

        var recovered = await turnStore.GetAsync(
            running.UserAor,
            running.AssistantNumber,
            running.Id);
        Assert.Equal(TurnState.Interrupted, recovered!.State);
        var message = Assert.Single(await messageStore.GetUnreadAsync(
            running.UserAor,
            running.AssistantNumber));
        Assert.Equal(running.Id, message.TurnId);
        Assert.Equal("任务因服务重启而中断", message.Text);
    }

    [Fact]
    public async Task CleanupRemovesExpiredThenReadAndProtectsActiveDeliveryAsync()
    {
        var config = this.CreateConfig(maxMessages: 2);
        var store = new FileMessageStore(config);
        var now = DateTimeOffset.UtcNow;

        await store.SaveAsync(CreateMessage("expired", DeliveryState.Unread, now.AddDays(-31)));
        await store.SaveAsync(CreateMessage("read", DeliveryState.Read, now.AddMinutes(-3)));
        await store.SaveAsync(CreateMessage("unread-1", DeliveryState.Unread, now.AddMinutes(-2)));
        await store.SaveAsync(CreateMessage("unread-2", DeliveryState.Unread, now.AddMinutes(-1)));
        await store.SaveAsync(CreateMessage("delivering", DeliveryState.Delivering, now.AddDays(-40)));

        var result = await store.CleanupAsync(now);

        Assert.Equal(1, result.ExpiredRemoved);
        Assert.Equal(2, result.OverflowRemoved);
        Assert.Null(await store.GetAsync("sip:1001@example.test", "10086", "expired"));
        Assert.Null(await store.GetAsync("sip:1001@example.test", "10086", "read"));
        Assert.NotNull(await store.GetAsync("sip:1001@example.test", "10086", "delivering"));
    }

    public void Dispose()
    {
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, true);
        }
    }

    private MessageStoreConfig CreateConfig(int maxMessages = 100, int recentTurns = 20) => new()
    {
        RootPath = this._root,
        RetentionDays = 30,
        MaxMessagesPerConversation = maxMessages,
        RecentConversationTurns = recentTurns
    };

    private static ConversationTurn CreateConversation(string id, DateTimeOffset completedAt) => new()
    {
        Id = id,
        UserAor = "sip:1001@example.test",
        AssistantNumber = "10086",
        UserText = $"user-{id}",
        AssistantText = $"assistant-{id}",
        CompletedAt = completedAt
    };

    private static MessageRecord CreateMessage(
        string id,
        DeliveryState state,
        DateTimeOffset createdAt) => new()
        {
            Id = id,
            TurnId = $"turn-{id}",
            UserAor = "sip:1001@example.test",
            AssistantNumber = "10086",
            Text = id,
            State = state,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
}
