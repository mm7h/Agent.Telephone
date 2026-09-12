namespace Agent.Telephone.Sample.Server.FunctionTools.Codex
{
    internal sealed record CodexTaskRequest(
        string Prompt,
        string UserAor,
        string AssistantNumber,
        bool StartNewTask);
}
