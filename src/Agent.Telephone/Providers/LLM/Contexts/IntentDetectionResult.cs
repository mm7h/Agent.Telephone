using System.ComponentModel;
using Agent.Telephone.Abstractions;

namespace Agent.Telephone.Providers.LLM.Contexts
{
    internal sealed record IntentDetectionResult([Description("是否检测到意图")] bool Detected, [Description("函数元数据")] FunctionMetadata? Function, [Description("用户消息")] string UserMessage);
}
