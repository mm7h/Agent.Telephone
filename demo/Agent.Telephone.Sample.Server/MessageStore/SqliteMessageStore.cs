using System.Globalization;
using Agent.Telephone.Abstractions.Persistence;
using Microsoft.Data.Sqlite;

namespace Agent.Telephone.Sample.Server.MessageStore
{
    public sealed class SqliteMessageStore : ITelephoneStore
    {
        private const string ConversationMessageColumns = "Id, TurnId, UserAor, AssistantNumber, Role, FullText, State, CreatedAt, UpdatedAt, ReadAt";
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

        public async Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            ValidateMessage(message);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ConversationMessages (Id, TurnId, UserAor, AssistantNumber, Role, FullText, State, CreatedAt, UpdatedAt, ReadAt, IsDelete)
                VALUES ($id, $turnId, $userAor, $assistantNumber, $role, $fullText, $state, $createdAt, $updatedAt, $readAt, 0);
                """;
            AddConversationMessageParameters(command, message);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveMessageSegmentAsync(MessageSegment segment, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (string.IsNullOrWhiteSpace(segment.Id) || string.IsNullOrWhiteSpace(segment.MessageId) || segment.Sequence < 1)
            {
                throw new ArgumentException("Segment identity and sequence must be specified.", nameof(segment));
            }

            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MessageSegments (Id, MessageId, Sequence, TextContent, AudioPath, CreatedAt)
                VALUES ($id, $messageId, $sequence, $textContent, $audioPath, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", segment.Id);
            command.Parameters.AddWithValue("$messageId", segment.MessageId);
            command.Parameters.AddWithValue("$sequence", segment.Sequence);
            command.Parameters.AddWithValue("$textContent", segment.TextContent);
            command.Parameters.AddWithValue("$audioPath", segment.AudioPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", segment.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public Task FinalizeAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string fullText,
            DeliveryState state,
            CancellationToken cancellationToken = default)
        {
            if (state is not DeliveryState.Unread and not DeliveryState.Read)
            {
                throw new ArgumentOutOfRangeException(nameof(state), "A completed assistant message must be unread or read.");
            }

            return this.UpdateAssistantMessageAsync(userAor, assistantNumber, messageId, fullText, state, cancellationToken);
        }

        public Task FailAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string fullText,
            CancellationToken cancellationToken = default) =>
            this.UpdateAssistantMessageAsync(userAor, assistantNumber, messageId, fullText, DeliveryState.Failed, cancellationToken);

        public async Task DiscardAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await BeginImmediateAsync(connection, cancellationToken);
            try
            {
                await using (SqliteCommand deleteSegments = connection.CreateCommand())
                {
                    deleteSegments.CommandText = "DELETE FROM MessageSegments WHERE MessageId = $messageId;";
                    deleteSegments.Parameters.AddWithValue("$messageId", messageId);
                    await deleteSegments.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (SqliteCommand deleteMessage = connection.CreateCommand())
                {
                    deleteMessage.CommandText = """
                        DELETE FROM ConversationMessages
                        WHERE Id = $messageId AND UserAor = $userAor AND AssistantNumber = $assistantNumber AND Role = $assistant;
                        """;
                    deleteMessage.Parameters.AddWithValue("$messageId", messageId);
                    deleteMessage.Parameters.AddWithValue("$userAor", userAor);
                    deleteMessage.Parameters.AddWithValue("$assistantNumber", assistantNumber);
                    deleteMessage.Parameters.AddWithValue("$assistant", (int)ConversationRole.Assistant);
                    await deleteMessage.ExecuteNonQueryAsync(cancellationToken);
                }

                await CommitAsync(connection, cancellationToken);
            }
            catch
            {
                await RollbackAsync(connection);
                throw;
            }
        }

        public async Task<IReadOnlyList<ConversationMessage>> GetUnreadAssistantMessagesAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {ConversationMessageColumns}
                FROM ConversationMessages
                WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND Role = $assistant AND State = $unread AND IsDelete = 0
                ORDER BY CreatedAt, Id;
                """;
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            command.Parameters.AddWithValue("$assistant", (int)ConversationRole.Assistant);
            command.Parameters.AddWithValue("$unread", (int)DeliveryState.Unread);
            return await ReadConversationMessagesAsync(command, cancellationToken);
        }

        public async Task<IReadOnlyList<ConversationMessage>> GetConversationMessagesAsync(
            string userAor,
            string assistantNumber,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {ConversationMessageColumns}
                FROM ConversationMessages
                WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND IsDelete = 0
                ORDER BY CreatedAt, Id;
                """;
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            return await ReadConversationMessagesAsync(command, cancellationToken);
        }

        public async Task<ConversationMessage?> GetAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException("Message id cannot be empty.", nameof(messageId));
            }

            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {ConversationMessageColumns}
                FROM ConversationMessages
                WHERE Id = $messageId
                  AND UserAor = $userAor
                  AND AssistantNumber = $assistantNumber
                  AND Role = $role
                  AND IsDelete = 0;
                """;
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            command.Parameters.AddWithValue("$role", (int)ConversationRole.Assistant);

            IReadOnlyList<ConversationMessage> messages = await ReadConversationMessagesAsync(command, cancellationToken);
            return messages.Count == 0 ? null : messages[0];
        }

        public async Task<IReadOnlyList<MessageSegment>> GetMessageSegmentsAsync(string messageId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException("Message id cannot be empty.", nameof(messageId));
            }

            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, MessageId, Sequence, TextContent, AudioPath, CreatedAt
                FROM MessageSegments
                WHERE MessageId = $messageId
                ORDER BY Sequence;
                """;
            command.Parameters.AddWithValue("$messageId", messageId);
            var segments = new List<MessageSegment>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                segments.Add(new MessageSegment
                {
                    Id = reader.GetString(0),
                    MessageId = reader.GetString(1),
                    Sequence = reader.GetInt64(2),
                    TextContent = reader.GetString(3),
                    AudioPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = ReadDateTimeOffset(reader, 5),
                });
            }

            return segments;
        }

        public Task MarkAssistantMessageReadAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken = default) =>
            this.UpdateAssistantMessageAsync(userAor, assistantNumber, messageId, null, DeliveryState.Read, cancellationToken);

        public async Task MarkAssistantMessagesReadAsync(
            string userAor,
            string assistantNumber,
            IReadOnlyCollection<string> messageIds,
            CancellationToken cancellationToken = default)
        {
            ValidateIdentity(userAor, assistantNumber);
            if (messageIds.Count == 0)
            {
                return;
            }

            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await BeginImmediateAsync(connection, cancellationToken);
            try
            {
                foreach (string messageId in messageIds.Distinct(StringComparer.Ordinal))
                {
                    await this.MarkAssistantMessageReadAsync(connection, userAor, assistantNumber, messageId, cancellationToken);
                }

                await CommitAsync(connection, cancellationToken);
            }
            catch
            {
                await RollbackAsync(connection);
                throw;
            }
        }

        public async Task<MessageCleanupResult> CleanupConversationMessagesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await BeginImmediateAsync(connection, cancellationToken);
            try
            {
                int expiredRemoved = await this.RemoveExpiredAsync(connection, now.AddDays(-this._retentionDays), now, cancellationToken);
                int overflowRemoved = await this.RemoveOverflowAsync(connection, now, cancellationToken);
                await CommitAsync(connection, cancellationToken);
                return new MessageCleanupResult(expiredRemoved, overflowRemoved);
            }
            catch
            {
                await RollbackAsync(connection);
                throw;
            }
        }

        public async Task SaveDeviceRegistrationAsync(DeviceRegistrationRecord registration, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ValidateRegistration(registration);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DeviceRegistrations (DeviceId, Aor, Contact, RegisteredAt, RefreshedAt, ExpiresAt, IsDelete)
                VALUES ($deviceId, $aor, $contact, $registeredAt, $refreshedAt, $expiresAt, 0)
                ON CONFLICT(DeviceId) DO UPDATE SET
                    Aor = excluded.Aor,
                    Contact = excluded.Contact,
                    RegisteredAt = excluded.RegisteredAt,
                    RefreshedAt = excluded.RefreshedAt,
                    ExpiresAt = excluded.ExpiresAt,
                    IsDelete = 0;
                """;
            AddRegistrationParameters(command, registration);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task RemoveDeviceRegistrationAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("Device id cannot be empty.", nameof(deviceId));
            }

            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = this._useLogicalDelete
                ? "UPDATE DeviceRegistrations SET IsDelete = 1 WHERE DeviceId = $deviceId AND IsDelete = 0;"
                : "DELETE FROM DeviceRegistrations WHERE DeviceId = $deviceId;";
            command.Parameters.AddWithValue("$deviceId", deviceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<DeviceRegistrationRecord>> GetActiveDeviceRegistrationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT DeviceId, Aor, Contact, RegisteredAt, RefreshedAt, ExpiresAt FROM DeviceRegistrations WHERE IsDelete = 0;";
            var registrations = new List<DeviceRegistrationRecord>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var registration = new DeviceRegistrationRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadDateTimeOffset(reader, 3),
                    ReadDateTimeOffset(reader, 4),
                    ReadDateTimeOffset(reader, 5));
                if (registration.ExpiresAt > now)
                {
                    registrations.Add(registration);
                }
            }

            return registrations;
        }

        private async Task UpdateAssistantMessageAsync(
            string userAor,
            string assistantNumber,
            string messageId,
            string? fullText,
            DeliveryState state,
            CancellationToken cancellationToken)
        {
            ValidateIdentity(userAor, assistantNumber);
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await this.UpdateAssistantMessageAsync(connection, userAor, assistantNumber, messageId, fullText, state, cancellationToken);
        }

        private async Task UpdateAssistantMessageAsync(
            SqliteConnection connection,
            string userAor,
            string assistantNumber,
            string messageId,
            string? fullText,
            DeliveryState state,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException("Message id cannot be empty.", nameof(messageId));
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ConversationMessages
                SET FullText = COALESCE($fullText, FullText), State = $state, UpdatedAt = $now,
                    ReadAt = CASE WHEN $state = $read THEN $now ELSE NULL END
                WHERE Id = $messageId AND UserAor = $userAor AND AssistantNumber = $assistantNumber
                    AND Role = $assistant AND IsDelete = 0;
                """;
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            command.Parameters.AddWithValue("$fullText", fullText ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$assistant", (int)ConversationRole.Assistant);
            command.Parameters.AddWithValue("$read", (int)DeliveryState.Read);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task MarkAssistantMessageReadAsync(
            SqliteConnection connection,
            string userAor,
            string assistantNumber,
            string messageId,
            CancellationToken cancellationToken) =>
            await this.UpdateAssistantMessageAsync(connection, userAor, assistantNumber, messageId, null, DeliveryState.Read, cancellationToken);

        private async Task<int> RemoveExpiredAsync(SqliteConnection connection, DateTimeOffset threshold, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = this._useLogicalDelete
                ? "UPDATE ConversationMessages SET IsDelete = 1, UpdatedAt = $now WHERE CreatedAt < $threshold AND IsDelete = 0;"
                : "DELETE FROM ConversationMessages WHERE CreatedAt < $threshold;";
            command.Parameters.AddWithValue("$threshold", threshold.ToString("O"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<int> RemoveOverflowAsync(SqliteConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var excessTurns = new List<(string UserAor, string AssistantNumber, string TurnId)>();
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT UserAor, AssistantNumber, TurnId
                    FROM (
                        SELECT UserAor, AssistantNumber, TurnId,
                               ROW_NUMBER() OVER (PARTITION BY UserAor, AssistantNumber ORDER BY CASE State WHEN $read THEN 0 ELSE 1 END, CreatedAt) AS Position
                        FROM ConversationMessages
                        WHERE Role = $assistant AND IsDelete = 0
                    )
                    WHERE Position > $maximum;
                    """;
                command.Parameters.AddWithValue("$assistant", (int)ConversationRole.Assistant);
                command.Parameters.AddWithValue("$read", (int)DeliveryState.Read);
                command.Parameters.AddWithValue("$maximum", this._maxMessagesPerConversation);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    excessTurns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            var removed = 0;
            foreach ((string userAor, string assistantNumber, string turnId) in excessTurns.Distinct())
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = this._useLogicalDelete
                    ? "UPDATE ConversationMessages SET IsDelete = 1, UpdatedAt = $now WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND TurnId = $turnId AND IsDelete = 0;"
                    : "DELETE FROM ConversationMessages WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber AND TurnId = $turnId;";
                command.Parameters.AddWithValue("$userAor", userAor);
                command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
                command.Parameters.AddWithValue("$turnId", turnId);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                removed += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return removed;
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            await this.EnsureInitializedAsync(cancellationToken);
            var connection = new SqliteConnection(this._connectionStringBuilder.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (this._initialized)
            {
                return;
            }

            await this._initializationGate.WaitAsync(cancellationToken);
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
                await connection.OpenAsync(cancellationToken);
                await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
                await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
                await ExecuteNonQueryAsync(connection, "DROP TABLE IF EXISTS Messages;", cancellationToken);
                await ExecuteNonQueryAsync(connection, """
                    CREATE TABLE IF NOT EXISTS ConversationMessages (
                        Id TEXT NOT NULL PRIMARY KEY,
                        TurnId TEXT NOT NULL,
                        UserAor TEXT NOT NULL,
                        AssistantNumber TEXT NOT NULL,
                        Role INTEGER NOT NULL,
                        FullText TEXT NOT NULL,
                        State INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        ReadAt TEXT NULL,
                        IsDelete INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS MessageSegments (
                        Id TEXT NOT NULL PRIMARY KEY,
                        MessageId TEXT NOT NULL,
                        Sequence INTEGER NOT NULL,
                        TextContent TEXT NOT NULL,
                        AudioPath TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        FOREIGN KEY (MessageId) REFERENCES ConversationMessages(Id) ON DELETE CASCADE,
                        UNIQUE(MessageId, Sequence)
                    );
                    CREATE TABLE IF NOT EXISTS DeviceRegistrations (
                        DeviceId TEXT NOT NULL PRIMARY KEY,
                        Aor TEXT NOT NULL,
                        Contact TEXT NOT NULL,
                        RegisteredAt TEXT NOT NULL,
                        RefreshedAt TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL,
                        IsDelete INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS IX_ConversationMessages_UnreadAssistant
                    ON ConversationMessages(UserAor, AssistantNumber, Role, State, IsDelete, CreatedAt);
                    CREATE INDEX IF NOT EXISTS IX_ConversationMessages_Cleanup
                    ON ConversationMessages(UserAor, AssistantNumber, Role, IsDelete, CreatedAt);
                    CREATE INDEX IF NOT EXISTS IX_MessageSegments_MessageSequence
                    ON MessageSegments(MessageId, Sequence);
                    """, cancellationToken);
                await EnsureColumnAsync(
                    connection,
                    "DeviceRegistrations",
                    "IsDelete",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken);
                await ExecuteNonQueryAsync(
                    connection,
                    "CREATE INDEX IF NOT EXISTS IX_DeviceRegistrations_ActiveExpiresAt ON DeviceRegistrations(IsDelete, ExpiresAt);",
                    cancellationToken);
                this._initialized = true;
            }
            finally
            {
                this._initializationGate.Release();
            }
        }

        private static async Task<IReadOnlyList<ConversationMessage>> ReadConversationMessagesAsync(SqliteCommand command, CancellationToken cancellationToken)
        {
            var messages = new List<ConversationMessage>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new ConversationMessage
                {
                    Id = reader.GetString(0),
                    TurnId = reader.GetString(1),
                    UserAor = reader.GetString(2),
                    AssistantNumber = reader.GetString(3),
                    Role = (ConversationRole)reader.GetInt32(4),
                    FullText = reader.GetString(5),
                    State = (DeliveryState)reader.GetInt32(6),
                    CreatedAt = ReadDateTimeOffset(reader, 7),
                    UpdatedAt = ReadDateTimeOffset(reader, 8),
                    ReadAt = reader.IsDBNull(9) ? null : ReadDateTimeOffset(reader, 9),
                });
            }

            return messages;
        }

        private static async Task EnsureColumnAsync(
            SqliteConnection connection,
            string table,
            string column,
            string definition,
            CancellationToken cancellationToken)
        {
            bool exists = false;
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            await reader.DisposeAsync();
            if (exists)
            {
                return;
            }

            await ExecuteNonQueryAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken);
        }

        private static void AddConversationMessageParameters(SqliteCommand command, ConversationMessage message)
        {
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$turnId", message.TurnId);
            command.Parameters.AddWithValue("$userAor", message.UserAor);
            command.Parameters.AddWithValue("$assistantNumber", message.AssistantNumber);
            command.Parameters.AddWithValue("$role", (int)message.Role);
            command.Parameters.AddWithValue("$fullText", message.FullText);
            command.Parameters.AddWithValue("$state", (int)message.State);
            command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", message.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$readAt", message.ReadAt?.ToString("O") ?? (object)DBNull.Value);
        }

        private static void AddRegistrationParameters(SqliteCommand command, DeviceRegistrationRecord registration)
        {
            command.Parameters.AddWithValue("$deviceId", registration.DeviceId);
            command.Parameters.AddWithValue("$aor", registration.Aor);
            command.Parameters.AddWithValue("$contact", registration.Contact);
            command.Parameters.AddWithValue("$registeredAt", registration.RegisteredAt.ToString("O"));
            command.Parameters.AddWithValue("$refreshedAt", registration.RefreshedAt.ToString("O"));
            command.Parameters.AddWithValue("$expiresAt", registration.ExpiresAt.ToString("O"));
        }

        private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal) =>
            DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        private static Task BeginImmediateAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);

        private static Task CommitAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);

        private static async Task RollbackAsync(SqliteConnection connection)
        {
            try
            {
                await ExecuteNonQueryAsync(connection, "ROLLBACK;", CancellationToken.None);
            }
            catch (SqliteException)
            {
            }
        }

        private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void ValidateMessage(ConversationMessage message)
        {
            ValidateIdentity(message.UserAor, message.AssistantNumber);
            if (string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.TurnId))
            {
                throw new ArgumentException("Message identity cannot be empty.", nameof(message));
            }
            if (message.Role == ConversationRole.User && message.State != DeliveryState.Read)
            {
                throw new ArgumentException("User messages must be read.", nameof(message));
            }
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

        private static void ValidateRegistration(DeviceRegistrationRecord registration)
        {
            if (string.IsNullOrWhiteSpace(registration.DeviceId) || string.IsNullOrWhiteSpace(registration.Aor) || string.IsNullOrWhiteSpace(registration.Contact))
            {
                throw new ArgumentException("Device registration identity cannot be empty.", nameof(registration));
            }
            if (registration.ExpiresAt <= registration.RefreshedAt)
            {
                throw new ArgumentException("Device registration must expire after it is refreshed.", nameof(registration));
            }
        }
    }
}
