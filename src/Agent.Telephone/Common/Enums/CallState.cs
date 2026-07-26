namespace Agent.Telephone.Common.Enums
{
    internal enum CallState
    {
        Idle = 0,
        PreparingAgent,
        AgentConnected,
        PlayingPrompt,
        TransferDialing,
        Bridged,
        CallbackDialing,
        CallbackConnected,
        Ending,
        Failed
    }
}
