namespace Agent.Telephone.Abstractions.Configs
{
    public sealed class PromptMediaConfig
    {
        public string AgentBusy { get; set; } = string.Empty;
        public string TransferWaiting { get; set; } = string.Empty;
        public string TransferFailed { get; set; } = string.Empty;
        public string TaskInterrupted { get; set; } = string.Empty;
    }
}
