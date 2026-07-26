using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;

namespace Agent.Telephone.Providers.Conversation.Persistence
{
    public sealed class FileMessageStore : IMessageStore
    {
        private readonly FileStoreLayout _layout;

        public FileMessageStore(MessageStoreConfig config)
        {
            _layout = new FileStoreLayout(config);
        }

        public async Task SaveAsync(
            MessageRecord message,
            ReadOnlyMemory<byte> wave = default,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            FileStoreLayout.ValidateIdentity(message.UserAor, message.AssistantNumber);

            var directory = GetDirectory(message.UserAor, message.AssistantNumber);
            var gate = FileStoreInfrastructure.GetLock(directory);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(directory);
                var fileName = FileStoreLayout.GetRecordFileName(message.Id);
                await FileStoreInfrastructure.WriteTextAsync(
                    Path.Combine(directory, $"{fileName}.txt"),
                    message.Text,
                    cancellationToken).ConfigureAwait(false);
                if (!wave.IsEmpty)
                {
                    await FileStoreInfrastructure.WriteBytesAsync(
                        Path.Combine(directory, $"{fileName}.wav"),
                        wave,
                        cancellationToken).ConfigureAwait(false);
                }

                await FileStoreInfrastructure.WriteJsonAsync(
                    Path.Combine(directory, $"{fileName}.json"),
                    new MessageMetadata(
                        message.Id,
                        message.TurnId,
                        message.UserAor,
                        message.AssistantNumber,
                        message.State,
                        message.CreatedAt,
                        message.UpdatedAt,
                        message.ReadAt),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<MessageRecord?> GetAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            FileStoreLayout.ValidateIdentity(userAor, assistantNumber);
            return ReadLockedAsync(
                GetDirectory(userAor, assistantNumber),
                messageId,
                cancellationToken);
        }

        public async Task<IReadOnlyList<MessageRecord>> GetUnreadAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default)
        {
            FileStoreLayout.ValidateIdentity(userAor, assistantNumber);
            var directory = GetDirectory(userAor, assistantNumber);
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var gate = FileStoreInfrastructure.GetLock(directory);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = new List<MessageRecord>();
                foreach (var metadata in await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false))
                {
                    if (metadata.State != DeliveryState.Unread)
                    {
                        continue;
                    }

                    var message = await ReadAsync(directory, metadata.Id, cancellationToken).ConfigureAwait(false);
                    if (message is not null)
                    {
                        result.Add(message);
                    }
                }

                return result.OrderBy(item => item.CreatedAt).ToArray();
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<bool> MarkReadAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            FileStoreLayout.ValidateIdentity(userAor, assistantNumber);
            var directory = GetDirectory(userAor, assistantNumber);
            var gate = FileStoreInfrastructure.GetLock(directory);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await ReadAsync(directory, messageId, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return false;
                }
                if (existing.State == DeliveryState.Read)
                {
                    return true;
                }

                var now = DateTimeOffset.UtcNow;
                var fileName = FileStoreLayout.GetRecordFileName(existing.Id);
                await FileStoreInfrastructure.WriteJsonAsync(
                    Path.Combine(directory, $"{fileName}.json"),
                    new MessageMetadata(
                        existing.Id,
                        existing.TurnId,
                        existing.UserAor,
                        existing.AssistantNumber,
                        DeliveryState.Read,
                        existing.CreatedAt,
                        now,
                        now),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<MessageCleanupResult> CleanupAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_layout.RootPath))
            {
                return new MessageCleanupResult(0, 0);
            }

            var expiredRemoved = 0;
            var overflowRemoved = 0;
            var directories = Directory.EnumerateDirectories(
                _layout.RootPath,
                "messages",
                SearchOption.AllDirectories).ToArray();

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gate = FileStoreInfrastructure.GetLock(directory);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var metadata = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
                    var retained = new List<MessageMetadata>();
                    var expiryThreshold = now.AddDays(-_layout.RetentionDays);
                    foreach (var item in metadata)
                    {
                        if (!IsProtected(item.State) && item.CreatedAt < expiryThreshold)
                        {
                            Delete(directory, item.Id);
                            expiredRemoved++;
                        }
                        else
                        {
                            retained.Add(item);
                        }
                    }

                    var excess = retained.Count - _layout.MaxMessagesPerConversation;
                    if (excess <= 0)
                    {
                        continue;
                    }

                    var candidates = retained
                        .Where(item => !IsProtected(item.State))
                        .OrderBy(item => CleanupPriority(item.State))
                        .ThenBy(item => item.CreatedAt)
                        .Take(excess)
                        .ToArray();
                    foreach (var item in candidates)
                    {
                        Delete(directory, item.Id);
                        overflowRemoved++;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            return new MessageCleanupResult(expiredRemoved, overflowRemoved);
        }

        private string GetDirectory(string userAor, string assistantNumber) =>
            Path.Combine(_layout.GetPartitionPath(userAor, assistantNumber), "messages");

        private static async Task<MessageRecord?> ReadAsync(
            string directory,
            string messageId,
            CancellationToken cancellationToken)
        {
            var fileName = FileStoreLayout.GetRecordFileName(messageId);
            var metadata = await FileStoreInfrastructure.ReadJsonAsync<MessageMetadata>(
                Path.Combine(directory, $"{fileName}.json"),
                cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                return null;
            }

            var textPath = Path.Combine(directory, $"{fileName}.txt");
            var wavePath = Path.Combine(directory, $"{fileName}.wav");
            return new MessageRecord
            {
                Id = metadata.Id,
                TurnId = metadata.TurnId,
                UserAor = metadata.UserAor,
                AssistantNumber = metadata.AssistantNumber,
                Text = await File.ReadAllTextAsync(textPath, cancellationToken).ConfigureAwait(false),
                State = metadata.State,
                CreatedAt = metadata.CreatedAt,
                UpdatedAt = metadata.UpdatedAt,
                ReadAt = metadata.ReadAt,
                WavePath = FileStoreInfrastructure.GetExistingPath(wavePath)
            };
        }

        private static async Task<MessageRecord?> ReadLockedAsync(
            string directory,
            string messageId,
            CancellationToken cancellationToken)
        {
            var gate = FileStoreInfrastructure.GetLock(directory);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadAsync(directory, messageId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<List<MessageMetadata>> ReadMetadataAsync(
            string directory,
            CancellationToken cancellationToken)
        {
            var result = new List<MessageMetadata>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await FileStoreInfrastructure.ReadJsonAsync<MessageMetadata>(
                    path,
                    cancellationToken).ConfigureAwait(false);
                if (metadata is not null)
                {
                    result.Add(metadata);
                }
            }

            return result;
        }

        private static bool IsProtected(DeliveryState state) =>
            state is DeliveryState.PendingCallback or DeliveryState.Dialing or DeliveryState.Delivering;

        private static int CleanupPriority(DeliveryState state) => state switch
        {
            DeliveryState.Expired => 0,
            DeliveryState.Read => 1,
            _ => 2
        };

        private static void Delete(string directory, string id)
        {
            var fileName = FileStoreLayout.GetRecordFileName(id);
            FileStoreInfrastructure.DeleteIfExists(Path.Combine(directory, $"{fileName}.json"));
            FileStoreInfrastructure.DeleteIfExists(Path.Combine(directory, $"{fileName}.txt"));
            FileStoreInfrastructure.DeleteIfExists(Path.Combine(directory, $"{fileName}.wav"));
        }

        private sealed record MessageMetadata(
            string Id,
            string TurnId,
            string UserAor,
            string AssistantNumber,
            DeliveryState State,
            DateTimeOffset CreatedAt,
            DateTimeOffset UpdatedAt,
            DateTimeOffset? ReadAt);
    }
}
