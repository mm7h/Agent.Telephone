using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Agent.Telephone.Sample.Server.FunctionTools.Codex
{
    internal sealed class CodexTaskRunner
    {
        // 当前单服务进程串行执行，避免重建后的工具实例并发续接同一 Thread。
        private static readonly SemaphoreSlim s_executionGate = new(1, 1);
        private readonly string _executablePath;
        private readonly string _workingDirectory;
        private readonly CodexThreadStore _threadStore;
        private readonly string _modelName;
        private readonly string _reasoningEffort;

        internal CodexTaskRunner(
            string executablePath,
            string workingDirectory,
            CodexThreadStore threadStore,
            string modelName,
            string reasoningEffort)
        {
            this._executablePath = executablePath;
            this._workingDirectory = workingDirectory;
            this._threadStore = threadStore;
            this._modelName = modelName;
            this._reasoningEffort = reasoningEffort;
        }

        internal async Task<string> RunAsync(CodexTaskRequest request, CancellationToken cancellationToken)
        {
            await s_executionGate.WaitAsync(cancellationToken);
            try
            {
                string? threadId = request.StartNewTask ? null
                    : await this._threadStore.GetThreadIdAsync(request.UserAor, request.AssistantNumber, cancellationToken);
                Directory.CreateDirectory(this._workingDirectory);
                string outputPath = Path.GetTempFileName();
                try
                {
                    return await this.ExecuteAsync(request, (threadId, outputPath), cancellationToken);
                }
                finally
                {
                    File.Delete(outputPath);
                }
            }
            finally
            {
                s_executionGate.Release();
            }
        }

        private async Task<string> ExecuteAsync(CodexTaskRequest request, (string? ThreadId, string OutputPath) session, CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = this._executablePath,
                WorkingDirectory = this._workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("exec");
            if (session.ThreadId is not null)
            {
                startInfo.ArgumentList.Add("resume");
                startInfo.ArgumentList.Add(session.ThreadId);
            }
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(this._modelName);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"model_reasoning_effort='{this._reasoningEffort}'");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--output-last-message");
            startInfo.ArgumentList.Add(session.OutputPath);
            startInfo.ArgumentList.Add("-");

            cancellationToken.ThrowIfCancellationRequested();
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            using CancellationTokenSource io = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task errors = DrainErrorsAsync(process.StandardError, io.Token);
            Task events = this.ReadEventsAsync(process.StandardOutput, request, io.Token);
            try
            {
                // 仅发送本次工具参数，不附带电话聊天记录；stdin 避免 CMD 解释指令中的特殊字符。
                await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), io.Token);
                process.StandardInput.Close();
                await events;
                await process.WaitForExitAsync(cancellationToken);
                await errors;
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Codex 以退出码 {process.ExitCode} 结束；请检查该任务后再决定是否继续。");
                }
                return (await File.ReadAllTextAsync(session.OutputPath, cancellationToken)).Trim();
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
                await process.WaitForExitAsync(cleanup.Token);
                try
                {
                    await Task.WhenAll(events, errors);
                }
                catch (Exception) when (events.IsFaulted || events.IsCanceled || errors.IsCanceled)
                {
                    // 主执行路径已传播错误；清理时观察所有读取任务，避免遗留后台读取。
                }
            }
        }

        private async Task ReadEventsAsync(StreamReader reader, CodexTaskRequest request, CancellationToken cancellationToken)
        {
            bool completed = false;
            bool hasThread = false;
            while (await reader.ReadLineAsync(cancellationToken) is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                using JsonDocument document = JsonDocument.Parse(line);
                string? type = document.RootElement.GetProperty("type").GetString();
                if (type == "thread.started")
                {
                    string? threadId = document.RootElement.GetProperty("thread_id").GetString();
                    if (!Guid.TryParse(threadId, out _))
                    {
                        throw new InvalidOperationException("Codex 返回了无效的 Thread ID。");
                    }
                    // 收到 ID 就保存，任务失败或取消后仍能明确续接，不会误建重复任务。
                    await this._threadStore.SaveThreadIdAsync(request.UserAor, request.AssistantNumber, threadId!, cancellationToken);
                    hasThread = true;
                }
                else if (type == "turn.failed")
                {
                    throw new InvalidOperationException("Codex 任务执行失败，已保留会话关联；不会自动重试或切换会话。");
                }
                else if (type == "turn.completed")
                {
                    completed = true;
                }
            }
            if (!hasThread || !completed)
            {
                throw new InvalidOperationException("Codex 未返回完整的会话和任务完成事件。");
            }
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
