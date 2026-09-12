using Microsoft.Data.Sqlite;

namespace Agent.Telephone.Sample.Server.FunctionTools.Codex
{
    internal sealed class CodexThreadStore
    {
        private static readonly SemaphoreSlim s_initializationGate = new(1, 1);
        private readonly SqliteConnectionStringBuilder _connectionStringBuilder;
        private bool _initialized;

        internal CodexThreadStore(string databasePath)
        {
            this._connectionStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 5,
            };
        }

        internal async Task<string?> GetThreadIdAsync(string userAor, string assistantNumber, CancellationToken cancellationToken)
        {
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT ThreadId FROM CodexThreads WHERE UserAor = $userAor AND AssistantNumber = $assistantNumber;";
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        internal async Task SaveThreadIdAsync(
            string userAor,
            string assistantNumber,
            string threadId,
            CancellationToken cancellationToken)
        {
            await using SqliteConnection connection = await this.OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO CodexThreads (UserAor, AssistantNumber, ThreadId)
                VALUES ($userAor, $assistantNumber, $threadId)
                ON CONFLICT(UserAor, AssistantNumber) DO UPDATE SET ThreadId = excluded.ThreadId;
                """;
            command.Parameters.AddWithValue("$userAor", userAor);
            command.Parameters.AddWithValue("$assistantNumber", assistantNumber);
            command.Parameters.AddWithValue("$threadId", threadId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            await this.EnsureInitializedAsync(cancellationToken);
            SqliteConnection connection = new(this._connectionStringBuilder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (this._initialized)
            {
                return;
            }
            await s_initializationGate.WaitAsync(cancellationToken);
            try
            {
                if (this._initialized)
                {
                    return;
                }
                await using SqliteConnection connection = new(this._connectionStringBuilder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS CodexThreads (
                        UserAor TEXT NOT NULL,
                        AssistantNumber TEXT NOT NULL,
                        ThreadId TEXT NOT NULL,
                        PRIMARY KEY (UserAor, AssistantNumber)
                    );
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                this._initialized = true;
            }
            finally
            {
                s_initializationGate.Release();
            }
        }
    }
}
