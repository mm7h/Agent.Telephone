namespace Agent.Telephone.Codex.Abstractions.Common.Enums
{
    /// <summary>
    /// Codex 指令的结束状态。
    /// </summary>
    public enum CodexExecutionStatus
    {
        /// <summary>
        /// 指令成功完成。
        /// </summary>
        Succeeded,
        /// <summary>
        /// 指令未完成。
        /// </summary>
        Failed,
        /// <summary>
        /// 指令因取消而结束。
        /// </summary>
        Cancelled,
    }
}
