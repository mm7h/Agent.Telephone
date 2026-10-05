using Microsoft.Extensions.AI;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Helpers;

namespace Agent.Telephone.Providers.LLM.Contexts
{
    /// <summary>
    /// 自定义函数工具在服务端内部的注册信息。
    /// </summary>
    internal sealed class FunctionToolRegistration
    {
        public FunctionToolRegistration(FunctionMetadata metadata, ToolAction toolAction)
        {
            this.Metadata = metadata;
            this.DefaultAction = toolAction;
            this.DtmfKeys = DtmfKey.None;
        }
        public FunctionToolRegistration(AIFunction function, ToolAction toolAction, string? preExecutionPrompt = null)
        {
            this.Function = function;
            this.Metadata = FunctionToolHelper.ToFunctionMetadata(function);
            this.DefaultAction = toolAction;
            this.DtmfKeys = DtmfKey.None;
            this.PreExecutionPrompt = preExecutionPrompt;
        }

        public FunctionToolRegistration(AIFunction function, FunctionMetadata metadata, ToolAction toolAction, DtmfKey dtmfKeys, string? preExecutionPrompt = null)
        {
            this.Function = function;
            this.Metadata = metadata;
            this.DefaultAction = toolAction;
            this.DtmfKeys = dtmfKeys;
            this.PreExecutionPrompt = preExecutionPrompt;
        }


        public AIFunction Function { get; private set; } = null!;

        public FunctionMetadata Metadata { get; }

        public ToolAction DefaultAction { get; }

        public DtmfKey DtmfKeys { get; }

        public string? PreExecutionPrompt { get; }

        public void WithFunction(AIFunction function)
        {
            this.Function = function;
        }
    }
}
