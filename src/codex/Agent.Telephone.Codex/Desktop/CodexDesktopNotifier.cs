using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Agent.Telephone.Codex.Desktop
{
    internal static class CodexDesktopNotifier
    {
        private const int MaxMessageLength = 1024 * 1024;
        private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

        internal static async Task InvalidateThreadListAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Desktop IPC 是 Windows 专属的可选刷新优化；其他平台仍可正常使用 CLI App Server。
                return;
            }

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await using NamedPipeClientStream pipe = new(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);

                string requestId = Guid.NewGuid().ToString("N");
                await WriteMessageAsync(
                    pipe,
                    new
                    {
                        type = "request",
                        requestId,
                        method = "initialize",
                        @params = new { clientType = "Agent.Telephone" },
                    },
                    timeout.Token);

                using JsonDocument response = await ReadMessageAsync(pipe, timeout.Token);
                if (!TryGetClientId(response.RootElement, requestId, out string? clientId))
                {
                    return;
                }

                await WriteMessageAsync(
                    pipe,
                    new
                    {
                        type = "broadcast",
                        method = "query-cache-invalidate",
                        sourceClientId = clientId,
                        version = 0,
                        @params = new { queryKey = Array.Empty<string>() },
                    },
                    timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            {
                // Desktop 未运行或升级了内部 IPC 时，不能影响电话任务。
            }
        }

        private static bool TryGetClientId(JsonElement response, string requestId, out string? clientId)
        {
            clientId = null;
            return response.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "response"
                && response.TryGetProperty("requestId", out JsonElement id)
                && id.GetString() == requestId
                && response.TryGetProperty("resultType", out JsonElement resultType)
                && resultType.GetString() == "success"
                && response.TryGetProperty("result", out JsonElement result)
                && result.TryGetProperty("clientId", out JsonElement value)
                && !string.IsNullOrWhiteSpace(clientId = value.GetString());
        }

        private static async Task WriteMessageAsync(Stream stream, object message, CancellationToken cancellationToken)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, s_jsonOptions);
            byte[] length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
            await stream.WriteAsync(length, cancellationToken);
            await stream.WriteAsync(json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task<JsonDocument> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] lengthBytes = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length <= 0 || length > MaxMessageLength)
            {
                throw new JsonException("Codex Desktop IPC returned an invalid message length.");
            }

            byte[] json = new byte[length];
            await stream.ReadExactlyAsync(json, cancellationToken);
            return JsonDocument.Parse(json);
        }
    }
}
