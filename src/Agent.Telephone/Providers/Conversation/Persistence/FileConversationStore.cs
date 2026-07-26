using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;

namespace Agent.Telephone.Providers.Conversation.Persistence
{
    public sealed class FileConversationStore : IConversationStore
    {
        private readonly FileStoreLayout _layout;
        private readonly IReadOnlyDictionary<string, int> _maximumTurnsByAssistant;

        public FileConversationStore(MessageStoreConfig config)
        {
            this._layout = new FileStoreLayout(config);
            this._maximumTurnsByAssistant = new Dictionary<string, int>();
        }

        public FileConversationStore(TelephoneConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            this._layout = new FileStoreLayout(config.MessageStoreConfig);
            this._maximumTurnsByAssistant = BuildAssistantLimits(config);
        }

        public async Task SaveAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(turn);
            FileStoreLayout.ValidateIdentity(turn.UserAor, turn.AssistantNumber);

            var directory = this.GetDirectory(turn.UserAor, turn.AssistantNumber);
            var gate = FileStoreInfrastructure.GetLock(directory);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(directory);
                var fileName = FileStoreLayout.GetRecordFileName(turn.Id);
                await FileStoreInfrastructure.WriteTextAsync(
                    Path.Combine(directory, $"{fileName}.user.txt"),
                    turn.UserText,
                    cancellationToken).ConfigureAwait(false);
                await FileStoreInfrastructure.WriteTextAsync(
                    Path.Combine(directory, $"{fileName}.assistant.txt"),
                    turn.AssistantText,
                    cancellationToken).ConfigureAwait(false);
                await FileStoreInfrastructure.WriteJsonAsync(
                    Path.Combine(directory, $"{fileName}.json"),
                    new ConversationMetadata(
                        turn.Id,
                        turn.UserAor,
                        turn.AssistantNumber,
                        turn.CompletedAt),
                    cancellationToken).ConfigureAwait(false);

                await this.TrimAsync(
                    directory,
                    this.GetMaximumTurns(turn.AssistantNumber),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<IReadOnlyList<ConversationTurn>> GetRecentAsync(
            string userAor,
            string assistantNumber,
            int? maximumTurns = null,
            CancellationToken cancellationToken = default)
        {
            FileStoreLayout.ValidateIdentity(userAor, assistantNumber);
            var maximum = maximumTurns ?? _layout.RecentConversationTurns;
            if (maximum <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTurns));
            }

            var directory = GetDirectory(userAor, assistantNumber);
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var gate = FileStoreInfrastructure.GetLock(directory);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var metadata = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
                var selected = metadata
                    .OrderByDescending(item => item.CompletedAt)
                    .Take(maximum)
                    .OrderBy(item => item.CompletedAt);
                var result = new List<ConversationTurn>();
                foreach (var item in selected)
                {
                    var fileName = FileStoreLayout.GetRecordFileName(item.Id);
                    result.Add(new ConversationTurn
                    {
                        Id = item.Id,
                        UserAor = item.UserAor,
                        AssistantNumber = item.AssistantNumber,
                        UserText = await File.ReadAllTextAsync(
                            Path.Combine(directory, $"{fileName}.user.txt"),
                            cancellationToken).ConfigureAwait(false),
                        AssistantText = await File.ReadAllTextAsync(
                            Path.Combine(directory, $"{fileName}.assistant.txt"),
                            cancellationToken).ConfigureAwait(false),
                        CompletedAt = item.CompletedAt
                    });
                }

                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        private string GetDirectory(string userAor, string assistantNumber) =>
            Path.Combine(this._layout.GetPartitionPath(userAor, assistantNumber), "conversation");

        private async Task TrimAsync(
            string directory,
            int maximumTurns,
            CancellationToken cancellationToken)
        {
            var metadata = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
            foreach (var item in metadata
                .OrderByDescending(value => value.CompletedAt)
                .Skip(maximumTurns))
            {
                var fileName = FileStoreLayout.GetRecordFileName(item.Id);
                FileStoreInfrastructure.DeleteIfExists(Path.Combine(directory, $"{fileName}.json"));
                FileStoreInfrastructure.DeleteIfExists(Path.Combine(directory, $"{fileName}.user.txt"));
                FileStoreInfrastructure.DeleteIfExists(Path.Combine(directory, $"{fileName}.assistant.txt"));
            }
        }

        private int GetMaximumTurns(string assistantNumber) =>
            this._maximumTurnsByAssistant.TryGetValue(assistantNumber, out int maximum)
                ? maximum
                : this._layout.RecentConversationTurns;

        private static IReadOnlyDictionary<string, int> BuildAssistantLimits(
            TelephoneConfig config)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            config.ModelConfig.ConfiguredSettings.TryGetValue(
                "Memory",
                out var memorySettings);
            foreach (AssistantConfig assistant in config.AssistantConfigs)
            {
                int maximum = config.MessageStoreConfig.RecentConversationTurns;
                if (memorySettings is not null &&
                    memorySettings.TryGetValue(assistant.Memory, out var memory) &&
                    memory.TryGetValue("MaximumTurns", out string? configured) &&
                    int.TryParse(configured, out int parsed) &&
                    parsed > 0)
                {
                    maximum = parsed;
                }
                result[assistant.DialingNumber] = maximum;
            }
            return result;
        }

        private static async Task<List<ConversationMetadata>> ReadMetadataAsync(
            string directory,
            CancellationToken cancellationToken)
        {
            var result = new List<ConversationMetadata>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await FileStoreInfrastructure.ReadJsonAsync<ConversationMetadata>(
                    path,
                    cancellationToken).ConfigureAwait(false);
                if (metadata is not null)
                {
                    result.Add(metadata);
                }
            }

            return result;
        }

        private sealed record ConversationMetadata(
            string Id,
            string UserAor,
            string AssistantNumber,
            DateTimeOffset CompletedAt);
    }
}
