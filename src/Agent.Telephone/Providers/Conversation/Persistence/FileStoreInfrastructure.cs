using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Telephone.Abstractions.Configs;

namespace Agent.Telephone.Providers.Conversation.Persistence
{
    internal sealed class FileStoreLayout
    {
        public FileStoreLayout(MessageStoreConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            if (string.IsNullOrWhiteSpace(config.RootPath))
            {
                throw new ArgumentException("Message store root path cannot be empty.", nameof(config));
            }
            if (config.RetentionDays <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config), "Retention days must be greater than zero.");
            }
            if (config.MaxMessagesPerConversation <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config), "Maximum messages must be greater than zero.");
            }
            if (config.RecentConversationTurns <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config), "Recent conversation turns must be greater than zero.");
            }

            this.RootPath = Path.GetFullPath(config.RootPath);
            this.RetentionDays = config.RetentionDays;
            this.MaxMessagesPerConversation = config.MaxMessagesPerConversation;
            this.RecentConversationTurns = config.RecentConversationTurns;
        }

        public string RootPath { get; }
        public int RetentionDays { get; }
        public int MaxMessagesPerConversation { get; }
        public int RecentConversationTurns { get; }

        public string GetPartitionPath(string userAor, string assistantNumber)
        {
            ValidateIdentity(userAor, assistantNumber);
            var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userAor))).ToLowerInvariant();
            var assistantSegment = Uri.EscapeDataString(assistantNumber);
            if (assistantSegment.Length > 80)
            {
                assistantSegment = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(assistantNumber))).ToLowerInvariant();
            }

            return Path.Combine(this.RootPath, userHash, $"assistant-{assistantSegment}");
        }

        public static string GetRecordFileName(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Record id cannot be empty.", nameof(id));
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        }

        public static void ValidateIdentity(string userAor, string assistantNumber)
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

    internal static class FileStoreInfrastructure
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static SemaphoreSlim GetLock(string directory) =>
            Locks.GetOrAdd(Path.GetFullPath(directory), _ => new SemaphoreSlim(1, 1));

        public static async Task WriteJsonAsync<T>(
            string path,
            T value,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await WriteBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<T?> ReadJsonAsync<T>(
            string path,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
            {
                return default;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        public static Task WriteTextAsync(
            string path,
            string value,
            CancellationToken cancellationToken) =>
            WriteBytesAsync(path, Encoding.UTF8.GetBytes(value), cancellationToken);

        public static async Task WriteBytesAsync(
            string path,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new ArgumentException("File path must include a directory.", nameof(path));
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public static string? GetExistingPath(string path) => File.Exists(path) ? Path.GetFullPath(path) : null;

        public static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
