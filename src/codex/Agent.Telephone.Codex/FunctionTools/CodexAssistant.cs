using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Codex.AppServer;
using Agent.Telephone.Codex.Abstractions.Common.Configs;
using Agent.Telephone.Codex.Abstractions.Common.Enums;
using Agent.Telephone.Codex.Abstractions.Common.Models;
using Agent.Telephone.Codex.Abstractions.Functions;
using Agent.Telephone.Codex.Desktop;
using Agent.Telephone.Codex.Persistence;
using Agent.Telephone.FunctionTools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Codex.FunctionTools
{
    internal sealed class CodexAssistant : PrivateFunctionTool
    {
        private static readonly SemaphoreSlim s_executionGate = new(1, 1);
        private readonly ICodexFunction _codexFunction;
        private readonly CodexThreadStore _threadStore;
        private readonly CodexAssistantOptions _options;

        public CodexAssistant(ICodexFunction codexFunction, CodexThreadStore threadStore, CodexAssistantOptions options)
        {
            this._codexFunction = codexFunction;
            this._threadStore = threadStore;
            this._options = options;
        }

        [Description("将用户明确交给 Codex 的指令交由本机 Codex 执行。只传本次任务指令，不要附带电话聊天历史或普通闲聊。同一任务的后续要求默认续接原 Codex 会话；用户明确开始新任务时将 startNewTask 设为 true。执行前系统会自动播报等待提示；本工具返回时执行已结束，请直接播报返回结果，不要再提示等待。用户挂断后完成的结果由系统回拨或留言投递。")]
        [ToolBehavior(ToolAction.Continue, PreExecutionPrompt = "好的，任务已交给 Codex，正在处理，请稍等。")]
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

            bool entered = false;
            try
            {
                await s_executionGate.WaitAsync(cancellationToken);
                entered = true;
                CodexConversationId? conversationId = startNewTask
                    ? null
                    : await this._threadStore.GetConversationIdAsync(userAor, assistantNumber, cancellationToken);
                this.Logger.LogInformation(
                    "[CodexInvocation] 开始调用 Codex CLI，任务模式：{TaskMode}。",
                    conversationId is null ? "新建" : "续接");
                CodexFunctionRequest request = new(taskPrompt, this._options.WorkingDirectory, conversationId);
                CodexFunctionResult result;
                if (this._codexFunction is ICodexFunctionLifecycle lifecycle)
                {
                    result = await lifecycle.ExecuteAsync(
                        request,
                        async (createdConversationId, token) =>
                        {
                            await this._threadStore.SaveConversationIdAsync(userAor, assistantNumber, createdConversationId, token);
                            await CodexDesktopNotifier.InvalidateThreadListAsync(token);
                        },
                        cancellationToken);
                }
                else
                {
                    result = await this._codexFunction.ExecuteAsync(request, cancellationToken);
                }

                if (result.ConversationId is not null)
                {
                    await this._threadStore.SaveConversationIdAsync(userAor, assistantNumber, result.ConversationId, cancellationToken);
                    if (conversationId is null && this._codexFunction is not ICodexFunctionLifecycle)
                    {
                        await CodexDesktopNotifier.InvalidateThreadListAsync(cancellationToken);
                    }
                }

                if (result.Status == CodexExecutionStatus.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                if (result.Status == CodexExecutionStatus.Failed)
                {
                    this.Logger.LogError("Codex task failed with {ErrorType}.", result.ErrorMessage);
                    return CreateResponse("Codex 任务未完成。请检查 Codex 登录、执行程序、工作目录和已有任务状态；系统不会自动重试或改用其他会话。");
                }

                return CreateResponse(string.IsNullOrWhiteSpace(result.Output) ? "Codex 任务已结束，但没有生成可播报的结果。" : result.Output);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or SqliteException or UnauthorizedAccessException)
            {
                this.Logger.LogError("Codex task failed with {ErrorType}.", exception.GetType().Name);
                return CreateResponse("Codex 任务未完成。请检查 Codex 登录、执行程序、工作目录和已有任务状态；系统不会自动重试或改用其他会话。");
            }
            finally
            {
                if (entered)
                {
                    s_executionGate.Release();
                }
            }
        }

        private static FunctionReturn<string> CreateResponse(string response)
        {
            return new FunctionReturn<string> { Result = response, Response = response };
        }
    }
}
