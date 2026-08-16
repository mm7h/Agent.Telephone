namespace Agent.Telephone.Abstractions.Common.Enums
{
    /// <summary>
    /// Assistant 切换状态。
    /// </summary>
    public enum AssistantSwitchStatus
    {
        Accepted = 0,
        InvalidTarget,
        UnknownAssistant,
        CurrentAssistant,
        CallEnded,
        Failed,
    }
}
