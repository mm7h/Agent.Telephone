using System.ComponentModel;
using System.Diagnostics;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Sample.Server.FunctionTools
{
    public class CodexAssistant : PrivateFunctionTool
    {
        private const string CodexExecutablePathEnvironmentVariable = "CODEX_CLI_PATH";

        [Description("将用户已经说明清楚的任务交给 Codex CLI 执行。任务可能耗时，用户可以挂断电话，完成后系统会回拨或留下留言。不要用于普通闲聊、简单问答或需要在执行中向用户确认的操作。")]
        [ToolBehavior(ToolAction.Continue)]
        public async Task<FunctionReturn<string>> RunCodexTaskAsync([Description("需要 Codex 执行的完整任务描述。")] string? taskPrompt, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskPrompt))
            {
                return CreateResponse("没有收到可执行的任务，请用户重新说明需要 Codex 完成什么。");
            }

            string? executablePath = Environment.GetEnvironmentVariable(CodexExecutablePathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = "codex";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--ephemeral");
            startInfo.ArgumentList.Add(taskPrompt);

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                this.Logger.LogError(exception, "无法启动 Codex CLI。请检查 {EnvironmentVariable} 或 PATH。", CodexExecutablePathEnvironmentVariable);
                return CreateResponse("无法启动 Codex CLI。请确认运行电话服务的账户已安装并登录 Codex CLI。");
            }

            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(standardOutputTask, standardErrorTask);
            }
            catch (OperationCanceledException)
            {
                TryStop(process);
                throw;
            }

            string result = (await standardOutputTask).Trim();
            if (process.ExitCode != 0)
            {
                this.Logger.LogWarning("Codex CLI 以退出码 {ExitCode} 结束。", process.ExitCode);
                return CreateResponse($"Codex 任务未完成，退出码为 {process.ExitCode}。请检查 Codex CLI 的登录状态和服务日志。");
            }
            if (string.IsNullOrWhiteSpace(result))
            {
                return CreateResponse("Codex 任务已结束，但没有生成可播报的结果。");
            }

            return CreateResponse(result);
        }

        private static FunctionReturn<string> CreateResponse(string response)
        {
            return new FunctionReturn<string>
            {
                Result = response,
                Response = response,
            };
        }

        private static void TryStop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
