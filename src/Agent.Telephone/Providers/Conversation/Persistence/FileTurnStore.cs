using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;

namespace Agent.Telephone.Providers.Conversation.Persistence
{
    public sealed class FileTurnStore : ITurnStore
    {
        private readonly FileStoreLayout _layout;

        public FileTurnStore(MessageStoreConfig config)
        {
            _layout = new FileStoreLayout(config);
        }

        public async Task SaveAsync(
            TurnRecord turn,
            ReadOnlyMemory<byte> responseWave = default,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(turn);
            FileStoreLayout.ValidateIdentity(turn.UserAor, turn.AssistantNumber);

            var directory = GetDirectory(turn.UserAor, turn.AssistantNumber);
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

                if (turn.ResponseText is not null)
                {
                    await FileStoreInfrastructure.WriteTextAsync(
                        Path.Combine(directory, $"{fileName}.response.txt"),
                        turn.ResponseText,
                        cancellationToken).ConfigureAwait(false);
                }
                if (!responseWave.IsEmpty)
                {
                    await FileStoreInfrastructure.WriteBytesAsync(
                        Path.Combine(directory, $"{fileName}.response.wav"),
                        responseWave,
                        cancellationToken).ConfigureAwait(false);
                }

                await FileStoreInfrastructure.WriteJsonAsync(
                    Path.Combine(directory, $"{fileName}.json"),
                    new TurnMetadata(
                        turn.Id,
                        turn.UserAor,
                        turn.AssistantNumber,
                        turn.State,
                        turn.StartedAt,
                        turn.UpdatedAt,
                        turn.CompletedAt,
                        turn.FailureReason),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<TurnRecord?> GetAsync(
            string userAor,
            string assistantNumber,
            string turnId,
            CancellationToken cancellationToken = default)
        {
            FileStoreLayout.ValidateIdentity(userAor, assistantNumber);
            var directory = GetDirectory(userAor, assistantNumber);
            return ReadAsync(directory, turnId, cancellationToken);
        }

        public async Task<IReadOnlyList<TurnRecord>> GetByStateAsync(
            TurnState state,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_layout.RootPath))
            {
                return [];
            }

            var result = new List<TurnRecord>();
            foreach (var metadataPath in Directory.EnumerateFiles(
                _layout.RootPath,
                "*.json",
                SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(metadataPath)),
                    "turns",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                var metadata = await FileStoreInfrastructure.ReadJsonAsync<TurnMetadata>(
                    metadataPath,
                    cancellationToken).ConfigureAwait(false);
                if (metadata is null || metadata.State != state)
                {
                    continue;
                }

                var turn = await ReadAsync(
                    Path.GetDirectoryName(metadataPath)!,
                    metadata.Id,
                    cancellationToken).ConfigureAwait(false);
                if (turn is not null)
                {
                    result.Add(turn);
                }
            }

            return result.OrderBy(item => item.StartedAt).ToArray();
        }

        private string GetDirectory(string userAor, string assistantNumber) =>
            Path.Combine(_layout.GetPartitionPath(userAor, assistantNumber), "turns");

        private static async Task<TurnRecord?> ReadAsync(
            string directory,
            string turnId,
            CancellationToken cancellationToken)
        {
            var fileName = FileStoreLayout.GetRecordFileName(turnId);
            var metadata = await FileStoreInfrastructure.ReadJsonAsync<TurnMetadata>(
                Path.Combine(directory, $"{fileName}.json"),
                cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                return null;
            }

            var userTextPath = Path.Combine(directory, $"{fileName}.user.txt");
            var responseTextPath = Path.Combine(directory, $"{fileName}.response.txt");
            var wavePath = Path.Combine(directory, $"{fileName}.response.wav");
            return new TurnRecord
            {
                Id = metadata.Id,
                UserAor = metadata.UserAor,
                AssistantNumber = metadata.AssistantNumber,
                UserText = await File.ReadAllTextAsync(userTextPath, cancellationToken).ConfigureAwait(false),
                ResponseText = File.Exists(responseTextPath)
                    ? await File.ReadAllTextAsync(responseTextPath, cancellationToken).ConfigureAwait(false)
                    : null,
                State = metadata.State,
                StartedAt = metadata.StartedAt,
                UpdatedAt = metadata.UpdatedAt,
                CompletedAt = metadata.CompletedAt,
                FailureReason = metadata.FailureReason,
                ResponseWavePath = FileStoreInfrastructure.GetExistingPath(wavePath)
            };
        }

        private sealed record TurnMetadata(
            string Id,
            string UserAor,
            string AssistantNumber,
            TurnState State,
            DateTimeOffset StartedAt,
            DateTimeOffset UpdatedAt,
            DateTimeOffset? CompletedAt,
            string? FailureReason);
    }
}
