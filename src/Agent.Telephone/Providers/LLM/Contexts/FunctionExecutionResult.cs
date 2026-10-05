using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Providers.LLM.Contexts
{
    internal sealed record FunctionExecutionResult(
        string FunctionName,
        string? Response,
        ToolAction Action,
        string UserMessage);
}
