using System.ComponentModel;
using System.Text.Json;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;
using Agent.Telephone.Sample.Server.MessageStore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Sample.Server.FunctionTools.Codex
{
    public class CodexAssistant : PrivateFunctionTool
    {
        private const string CODEX_PROJECT_NAME = "电话对话助手"; // codex 项目名称替换为你期望的项目名称
        private const string CODEX_PATH = "D:\\nodejs\\node_global\\codex.cmd"; // codex cli 路径替换为你真实的路径
        private const string CODEX_MODEL = "gpt-5.6-terra"; // codex 模型名称替换为你账户可用的模型
        private const string CODEX_REASONING_EFFORT = "medium"; // 可选：minimal、low、medium、high、xhigh（取决于模型支持）
        private readonly CodexTaskRunner _runner;

        public CodexAssistant() : this(
            CODEX_PATH,
            Path.Combine("data", "codex", CODEX_PROJECT_NAME),
            new CodexThreadStore(new SqliteMessageStoreOptions().DatabasePath),
            CODEX_MODEL,
            CODEX_REASONING_EFFORT)
        {
        }

        internal CodexAssistant(
            string executablePath,
            string workingDirectory,
            CodexThreadStore threadStore,
            string modelName,
            string reasoningEffort)
        {
            this._runner = new CodexTaskRunner(
                executablePath,
                workingDirectory,
                threadStore,
                modelName,
                reasoningEffort);
        }

        [Description("将用户明确交给 Codex 的指令交由 CLI 执行。只传本次任务指令，不要附带电话聊天历史或普通闲聊。同一任务的后续要求默认续接原 Codex 会话；用户明确开始新任务时将 startNewTask 设为 true。任务可能耗时，用户可以挂断电话，完成后系统会回拨或留下留言。")]
        [ToolBehavior(ToolAction.Continue)]
        public async Task<FunctionReturn<string>> RunCodexTaskAsync([Description("本次明确交给 Codex 的完整指令，不包含电话聊天历史。")] string? taskPrompt, [Description("用户明确开始新任务时为 true；同一任务的补充或修改为 false。")] bool startNewTask = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskPrompt))
            {
                return CreateResponse("没有收到可执行的任务，请用户重新说明需要 Codex 完成什么。");
            }
            string? userAor = this.CallControl?.UserAor;
            string? assistantNumber = this.CallControl?.AssistantNumber;
            if (string.IsNullOrWhiteSpace(userAor) || string.IsNullOrWhiteSpace(assistantNumber))
            {
                return CreateResponse("无法确认用户或助手身份，未执行 Codex 指令。");
            }
            try
            {
                CodexTaskRequest request = new(taskPrompt, userAor, assistantNumber, startNewTask);
                string result = await this._runner.RunAsync(request, cancellationToken);
                return CreateResponse(string.IsNullOrWhiteSpace(result) ? "Codex 任务已结束，但没有生成可播报的结果。" : result);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or JsonException or SqliteException or UnauthorizedAccessException or KeyNotFoundException)
            {
                this.Logger.LogError("Codex 任务失败，错误类型为 {ErrorType}。", exception.GetType().Name);
                return CreateResponse("Codex 任务未完成。请检查 CLI 登录、工作目录和已有任务状态；系统不会自动重试或改用其他会话。");
            }
        }

        private static FunctionReturn<string> CreateResponse(string response)
        {
            return new FunctionReturn<string> { Result = response, Response = response };
        }
    }
}
