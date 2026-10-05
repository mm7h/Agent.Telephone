using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.Common.Contexts
{
    /// <summary>
    /// 助手语音切换的结果。
    /// </summary>
    /// <param name="Status">助手切换状态。</param>
    /// <param name="TargetAssistantNumber">请求的助手拨号号码。</param>
    /// <param name="Message">描述结果的可选消息。</param>
    public sealed record AssistantSwitchResult(AssistantSwitchStatus Status, string TargetAssistantNumber, string? Message = null)
    {
        /// <summary>
        /// 切换请求是否被接受。
        /// </summary>
        public bool Succeeded => this.Status == AssistantSwitchStatus.Accepted;
    }
}
