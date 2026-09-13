using Agent.Telephone.Codex.Abstractions.Common.Models;

namespace Agent.Telephone.Codex.Abstractions.Functions
{
    /// <summary>
    /// 执行或续接 Codex Thread 的公共能力。
    /// </summary>
    public interface ICodexFunction
    {
        /// <summary>
        /// 执行本次 Codex 指令。
        /// </summary>
        /// <param name="request">本次指令及关联的 Thread。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>执行结果。</returns>
        Task<CodexFunctionResult> ExecuteAsync(CodexFunctionRequest request, CancellationToken cancellationToken = default);
    }
}
