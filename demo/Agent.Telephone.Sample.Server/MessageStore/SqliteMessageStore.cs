using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Sample.Server.MessageStore.Entities;
using Microsoft.Data.Sqlite;

namespace Agent.Telephone.Sample.Server.MessageStore
{
    public sealed class SqliteMessageStore : IMessageStore
    {
        private const string MessageColumns = "Id, TurnId, UserAor, AssistantNumber, AudioPath, Sequence, State, CreatedAt, UpdatedAt, ReadAt, IsDelete";

        private readonly SqliteConnectionStringBuilder _connectionStringBuilder;
        private readonly int _retentionDays;
        private readonly int _maxMessagesPerConversation;
        private readonly bool _useLogicalDelete;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private bool _initialized;

        public SqliteMessageStore(SqliteMessageStoreOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.DatabasePath))
            {
                throw new ArgumentException("Database path cannot be empty.", nameof(options));
            }
            if (options.RetentionDays <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Retention days must be greater than zero.");
            }
            if (options.MaxMessagesPerConversation <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Maximum messages per conversation must be greater than zero.");
            }
            if (options.DefaultTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Default timeout must be greater than zero.");
            }

            this._connectionStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(options.DatabasePath),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = options.DefaultTimeoutSeconds,
            };
            this._retentionDays = options.RetentionDays;
            this._maxMessagesPerConversation = options.MaxMessagesPerConversation;
            this._useLogicalDelete = options.UseLogicalDelete;
        }

        public async Task<MessageRecord> SaveAsync(
            MessageRecord message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            ValidateIdentity(message.UserAor, message.AssistantNumber);
            if (string.IsNullOrWhiteSpace(message.Id))
            {
                throw new ArgumentException("Message id cannot be empty.", nameof(message));
            }

            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                long? existingSequence = await GetSequenceAsync(connection, message.Id, cancellationToken).ConfigureAwait(false);
                long sequence = existingSequence ?? (message.Sequence > 0
                    ? message.Sequence
                    : await GetNextSequenceAsync(connection, message.UserAor, message.AssistantNumber, cancellationToken).ConfigureAwait(false));
                MessageRecord stored = message with { Sequence = sequence };
                await UpsertAsync(connection, stored, cancellationToken).ConfigureAwait(false);
                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
                return stored;
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<MessageRecord?> GetAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {MessageColumns}
                FROM Messages
                WHERE Id = $id AND UserAor = $userAor AND AssistantNumber = $assistantNumber AND IsDelete = 0;
                """;
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? SqliteMessageEntity.FromReader(reader).ToRecord()
                : null;
        }

        public async Task<IReadOnlyList<MessageRecord>> GetUnreadAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {MessageColumns}
                FROM Messages
                WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND State = $unread AND IsDelete = 0
                ORDER BY Sequence;
                """;
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            command.Parameters.AddWithValue("$unread", (int)DeliveryState.Unread);
            return await ReadMessagesAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> MarkReadAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Messages
                SET State = $read, UpdatedAt = $now, ReadAt = $now
                WHERE Id = $id AND UserAor = $userAor AND AssistantNumber = $assistantNumber AND State <> $read AND IsDelete = 0;
                """;
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            command.Parameters.AddWithValue("$read", (int)DeliveryState.Read);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            int updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return updated > 0 || await this.GetAsync(userAor, assistantNumber, messageId, cancellationToken).ConfigureAwait(false) is not null;
        }

        public async Task<MessageCleanupResult> CleanupAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                int expiredRemoved = await this.RemoveExpiredAsync(connection, now.AddDays(-this._retentionDays), now, cancellationToken).ConfigureAwait(false);
                int overflowRemoved = await this.DeleteOverflowAsync(connection, cancellationToken).ConfigureAwait(false);
                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
                return new MessageCleanupResult(expiredRemoved, overflowRemoved);
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            await this.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var connection = new SqliteConnection(this._connectionStringBuilder.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (this._initialized)
            {
                return;
            }

            await this._initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (this._initialized)
                {
                    return;
                }

                string? directory = Path.GetDirectoryName(this._connectionStringBuilder.DataSource);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var connection = new SqliteConnection(this._connectionStringBuilder.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, """
                    CREATE TABLE IF NOT EXISTS Messages (
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
                        IsDelete INTEGER NOT NULL DEFAULT 0,
                        UNIQUE(UserAor, AssistantNumber, Sequence)
                    );
                    """, cancellationToken).ConfigureAwait(false);
                await EnsureIsDeleteColumnAsync(connection, cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, """
                    CREATE INDEX IF NOT EXISTS IX_Messages_ActiveUnreadPlayback
                    ON Messages(UserAor, AssistantNumber, IsDelete, State, Sequence);
                    CREATE INDEX IF NOT EXISTS IX_Messages_ActivePartitionCleanup
                    ON Messages(UserAor, AssistantNumber, IsDelete, State, CreatedAt);
                    """, cancellationToken).ConfigureAwait(false);
                this._initialized = true;
            }
            finally
            {
                this._initializationGate.Release();
            }
        }

        private static async Task<long?> GetSequenceAsync(
            SqliteConnection connection,
            string messageId,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT Sequence FROM Messages WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", messageId);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null || result is DBNull ? null : Convert.ToInt64(result);
        }

        private static async Task<long> GetNextSequenceAsync(
            SqliteConnection connection,
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(MAX(Sequence), 0) + 1
                FROM Messages
                WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber;
                """;
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result);
        }

        private static async Task UpsertAsync(
            SqliteConnection connection,
            MessageRecord message,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Messages (Id, TurnId, UserAor, AssistantNumber, AudioPath, Sequence, State, CreatedAt, UpdatedAt, ReadAt, IsDelete)
                VALUES ($id, $turnId, $userAor, $assistantNumber, $audioPath, $sequence, $state, $createdAt, $updatedAt, $readAt, 0)
                ON CONFLICT(Id) DO UPDATE SET
                    TurnId = excluded.TurnId,
                    UserAor = excluded.UserAor,
                    AssistantNumber = excluded.AssistantNumber,
                    AudioPath = excluded.AudioPath,
                    Sequence = excluded.Sequence,
                    State = excluded.State,
                    CreatedAt = excluded.CreatedAt,
                    UpdatedAt = excluded.UpdatedAt,
                    ReadAt = excluded.ReadAt,
                    IsDelete = 0;
                """;
            AddMessageParameters(command, message);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> RemoveExpiredAsync(
            SqliteConnection connection,
            DateTimeOffset expiryThreshold,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = this._useLogicalDelete
                ? "UPDATE Messages SET IsDelete = 1, UpdatedAt = $now WHERE CreatedAt < $expiryThreshold AND IsDelete = 0;"
                : "DELETE FROM Messages WHERE CreatedAt < $expiryThreshold;";
            command.Parameters.AddWithValue("$expiryThreshold", expiryThreshold.ToString("O"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> DeleteOverflowAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            var partitions = new List<(string UserAor, string AssistantNumber, long Count)>();
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT UserAor, AssistantNumber, COUNT(*)
                    FROM Messages
                    WHERE IsDelete = 0
                    GROUP BY UserAor, AssistantNumber
                    HAVING COUNT(*) > $maximum;
                    """;
                command.Parameters.AddWithValue("$maximum", this._maxMessagesPerConversation);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    partitions.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            var removed = 0;
            foreach ((string userAor, string assistantNumber, long count) in partitions)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = this._useLogicalDelete
                    ? """
                        UPDATE Messages
                        SET IsDelete = 1
                        WHERE Id IN (
                            SELECT Id
                            FROM Messages
                            WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND IsDelete = 0
                            ORDER BY CASE State WHEN $read THEN 0 ELSE 1 END, CreatedAt
                            LIMIT $excess
                        );
                        """
                    : """
                        DELETE FROM Messages
                        WHERE Id IN (
                            SELECT Id
                            FROM Messages
                            WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND IsDelete = 0
                            ORDER BY CASE State WHEN $read THEN 0 ELSE 1 END, CreatedAt
                            LIMIT $excess
                        );
                        """;
                command.Parameters.AddWithValue("$userAor", userAor);
                command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
                command.Parameters.AddWithValue("$read", (int)DeliveryState.Read);
                command.Parameters.AddWithValue("$excess", count - this._maxMessagesPerConversation);
                removed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }

        private static async Task<IReadOnlyList<MessageRecord>> ReadMessagesAsync(
            SqliteCommand command,
            CancellationToken cancellationToken)
        {
            var messages = new List<MessageRecord>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(SqliteMessageEntity.FromReader(reader).ToRecord());
            }

            return messages;
        }

        private static void AddMessageParameters(SqliteCommand command, MessageRecord message)
        {
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$turnId", message.TurnId);
            command.Parameters.AddWithValue("$userAor", message.UserAor);
            command.Parameters.AddWithValue("$assistantNumber", message.AssistantNumber);
            command.Parameters.AddWithValue("$audioPath", message.AudioPath);
            command.Parameters.AddWithValue("$sequence", message.Sequence);
            command.Parameters.AddWithValue("$state", (int)message.State);
            command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", message.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$readAt", message.ReadAt?.ToString("O") ?? (object)DBNull.Value);
        }

        private static async Task EnsureIsDeleteColumnAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            var exists = false;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(Messages);";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(reader.GetString(1), "IsDelete", StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists)
            {
                return;
            }

            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE Messages ADD COLUMN IsDelete INTEGER NOT NULL DEFAULT 0;",
                cancellationToken).ConfigureAwait(false);
        }

        private static Task BeginImmediateAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);

        private static Task CommitAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);

        private static async Task RollbackAsync(SqliteConnection connection)
        {
            try
            {
                await ExecuteNonQueryAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
            }
        }

        private static async Task ExecuteNonQueryAsync(
            SqliteConnection connection,
            string commandText,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void ValidateIdentity(string userAor, string assistantNumber)
        {
            if (string.IsNullOrWhiteSpace(userAor))
            {
                throw new ArgumentException("User AOR cannot be empty.", nameof(userAor));
            }
            if (string.IsNullOrWhiteSpace(assistantNumber))
            {
                throw new ArgumentException("Assistant number cannot be empty.", nameof(assistantNumber));
            }
        }
    }
}
