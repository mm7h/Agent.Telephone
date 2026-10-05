using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agent.Telephone.Codex.Abstractions.Common.Configs;
using Agent.Telephone.Codex.Abstractions.Common.Enums;
using Agent.Telephone.Codex.Abstractions.Common.Models;

namespace Agent.Telephone.Codex.AppServer
{
    internal sealed class CodexAppServerFunction : ICodexFunctionLifecycle
    {
        // ponytail: 服务级串行化避免不同通话同时续接同一 Thread；吞吐成为瓶颈时改为按 Thread 加锁。
        private static readonly SemaphoreSlim s_executionGate = new(1, 1);
        private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
        private readonly CodexAssistantOptions _options;

        public CodexAppServerFunction(CodexAssistantOptions options)
        {
            this._options = options;
        }

        public async Task<CodexFunctionResult> ExecuteAsync(CodexFunctionRequest request, CancellationToken cancellationToken = default)
        {
            return await this.ExecuteAsync(request, static (_, _) => Task.CompletedTask, cancellationToken);
        }

        public async Task<CodexFunctionResult> ExecuteAsync(
            CodexFunctionRequest request,
            Func<CodexConversationId, CancellationToken, Task> onConversationCreated,
            CancellationToken cancellationToken)
        {
            CodexConversationId? conversationId = request.ConversationId;
            bool entered = false;
            try
            {
                await s_executionGate.WaitAsync(cancellationToken);
                entered = true;
                Directory.CreateDirectory(this._options.WorkingDirectory);
                return await this.ExecuteProcessAsync(request, onConversationCreated, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CodexFunctionResult(CodexExecutionStatus.Cancelled, string.Empty, conversationId);
            }
            catch (Exception exception)
            {
                return new CodexFunctionResult(CodexExecutionStatus.Failed, string.Empty, conversationId, exception.GetType().Name);
            }
            finally
            {
                if (entered)
                {
                    s_executionGate.Release();
                }
            }
        }

        private async Task<CodexFunctionResult> ExecuteProcessAsync(
            CodexFunctionRequest request,
            Func<CodexConversationId, CancellationToken, Task> onConversationCreated,
            CancellationToken cancellationToken)
        {
            CodexConversationId? conversationId = request.ConversationId;
            ProcessStartInfo startInfo = new()
            {
                FileName = this._options.ExecutablePath,
                WorkingDirectory = this._options.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("app-server");

            cancellationToken.ThrowIfCancellationRequested();
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            using CancellationTokenSource io = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task errors = DrainErrorsAsync(process.StandardError, io.Token);
            try
            {
                await SendRequestAsync(
                    process.StandardInput,
                    1,
                    "initialize",
                    new { clientInfo = new { name = "Agent.Telephone", version = "1.0" } },
                    io.Token);
                await ReadResponseAsync(process.StandardOutput, 1, io.Token);
                await SendNotificationAsync(process.StandardInput, "initialized", new { }, io.Token);

                if (conversationId is null)
                {
                    JsonElement threadResponse = await SendAndReadAsync(
                        process.StandardInput,
                        process.StandardOutput,
                        2,
                        "thread/start",
                        new
                        {
                            cwd = this._options.WorkingDirectory,
                            model = this._options.ModelName,
                            ephemeral = false,
                            approvalPolicy = "never",
                            sandbox = "read-only",
                        },
                        io.Token);
                    conversationId = GetRequiredConversationId(threadResponse);
                    await onConversationCreated(conversationId, io.Token);
                }
                else
                {
                    JsonElement threadResponse = await SendAndReadAsync(
                        process.StandardInput,
                        process.StandardOutput,
                        2,
                        "thread/resume",
                        new
                        {
                            threadId = conversationId.Value,
                            cwd = this._options.WorkingDirectory,
                            model = this._options.ModelName,
                            approvalPolicy = "never",
                            sandbox = "read-only",
                        },
                        io.Token);
                    CodexConversationId resumedConversationId = GetRequiredConversationId(threadResponse);
                    if (!string.Equals(conversationId.Value, resumedConversationId.Value, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Codex returned a different conversation ID when resuming.");
                    }
                }

                JsonElement turnResponse = await SendAndReadAsync(
                    process.StandardInput,
                    process.StandardOutput,
                    3,
                    "turn/start",
                    new
                    {
                        threadId = conversationId.Value,
                        input = new[] { new { type = "text", text = request.Prompt } },
                        effort = this._options.ReasoningEffort,
                    },
                    io.Token);
                string turnId = GetRequiredTurnId(turnResponse);
                string output = await this.ReadTurnResultAsync(process.StandardOutput, conversationId.Value, turnId, io.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
                await errors;
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("Codex app-server exited with a non-zero exit code.");
                }

                return new CodexFunctionResult(CodexExecutionStatus.Succeeded, output.Trim(), conversationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CodexFunctionResult(CodexExecutionStatus.Cancelled, string.Empty, conversationId);
            }
            catch (Exception exception)
            {
                return new CodexFunctionResult(CodexExecutionStatus.Failed, string.Empty, conversationId, exception.GetType().Name);
            }
            finally
            {
                io.Cancel();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }

                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(cleanup.Token);
                }
                catch (OperationCanceledException)
                {
                }

                try
                {
                    await errors;
                }
                catch (Exception) when (errors.IsFaulted || errors.IsCanceled)
                {
                    // 主执行路径已经归一化为 CodexFunctionResult。
                }
            }
        }

        private async Task<string> ReadTurnResultAsync(StreamReader reader, string conversationId, string turnId, CancellationToken cancellationToken)
        {
            string? finalMessage = null;
            while (await reader.ReadLineAsync(cancellationToken) is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("method", out JsonElement methodElement))
                {
                    continue;
                }

                if (!root.TryGetProperty("params", out JsonElement parameters) || !BelongsToTurn(parameters, conversationId, turnId))
                {
                    continue;
                }

                string? method = methodElement.GetString();
                if (method == "item/completed"
                    && parameters.TryGetProperty("item", out JsonElement item)
                    && item.TryGetProperty("type", out JsonElement itemType)
                    && itemType.GetString() == "agentMessage"
                    && item.TryGetProperty("text", out JsonElement text))
                {
                    finalMessage = text.GetString();
                }
                else if (method == "turn/failed")
                {
                    throw new InvalidOperationException("Codex turn failed.");
                }
                else if (method == "turn/completed")
                {
                    if (!parameters.TryGetProperty("turn", out JsonElement turn)
                        || !turn.TryGetProperty("status", out JsonElement status)
                        || status.GetString() != "completed")
                    {
                        throw new InvalidOperationException("Codex turn did not complete.");
                    }

                    return finalMessage ?? string.Empty;
                }
            }

            throw new InvalidOperationException("Codex did not return a completed turn.");
        }

        private static bool BelongsToTurn(JsonElement parameters, string conversationId, string turnId)
        {
            return parameters.TryGetProperty("threadId", out JsonElement eventConversationId)
                && eventConversationId.GetString() == conversationId
                && ((parameters.TryGetProperty("turnId", out JsonElement eventTurnId) && eventTurnId.GetString() == turnId)
                    || (parameters.TryGetProperty("turn", out JsonElement turn)
                        && turn.TryGetProperty("id", out JsonElement eventTurn)
                        && eventTurn.GetString() == turnId));
        }

        private static CodexConversationId GetRequiredConversationId(JsonElement response)
        {
            if (!response.TryGetProperty("thread", out JsonElement thread)
                || !thread.TryGetProperty("id", out JsonElement threadId)
                || string.IsNullOrWhiteSpace(threadId.GetString()))
            {
                throw new InvalidOperationException("Codex returned an invalid conversation ID.");
            }

            return new CodexConversationId(threadId.GetString()!);
        }

        private static string GetRequiredTurnId(JsonElement response)
        {
            if (!response.TryGetProperty("turn", out JsonElement turn)
                || !turn.TryGetProperty("id", out JsonElement turnId)
                || string.IsNullOrWhiteSpace(turnId.GetString()))
            {
                throw new InvalidOperationException("Codex did not return a valid turn ID.");
            }

            return turnId.GetString()!;
        }

        private static async Task<JsonElement> SendAndReadAsync(StreamWriter writer, StreamReader reader, int id, string method, object parameters, CancellationToken cancellationToken)
        {
            await SendRequestAsync(writer, id, method, parameters, cancellationToken);
            return await ReadResponseAsync(reader, id, cancellationToken);
        }

        private static async Task SendRequestAsync(StreamWriter writer, int id, string method, object parameters, CancellationToken cancellationToken)
        {
            await WriteMessageAsync(writer, new { id, method, @params = parameters }, cancellationToken);
        }

        private static async Task SendNotificationAsync(StreamWriter writer, string method, object parameters, CancellationToken cancellationToken)
        {
            await WriteMessageAsync(writer, new { method, @params = parameters }, cancellationToken);
        }

        private static async Task WriteMessageAsync(StreamWriter writer, object message, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(message, s_jsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        private static async Task<JsonElement> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken cancellationToken)
        {
            while (await reader.ReadLineAsync(cancellationToken) is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("id", out JsonElement id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt32(out int responseId)
                    || responseId != expectedId)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out _))
                {
                    throw new InvalidOperationException("Codex rejected the request.");
                }
                if (root.TryGetProperty("result", out JsonElement result))
                {
                    return result.Clone();
                }

                throw new InvalidOperationException("Codex returned an invalid response.");
            }

            throw new InvalidOperationException("Codex exited before returning a response.");
        }

        private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            char[] buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0)
            {
                // 排空诊断流以避免阻塞；不把可能含任务内容的 stderr 写入电话日志。
            }
        }
    }
}
