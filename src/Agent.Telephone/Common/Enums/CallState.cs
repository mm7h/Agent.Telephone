namespace Agent.Telephone.Common.Enums
{
    internal enum CallState
    {
        Idle = 0,
        PreparingAgent,
        AgentConnected,
        PlayingPrompt,
        CallbackDialing,
        CallbackConnected,
        Ending,
        Failed
    }
}
