using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;

namespace Agent.Telephone.FunctionTools
{
    internal sealed class AssistantSwitchFunctionTool : PrivateFunctionTool
    {
        [Description("Switches the current call to the agent for the specified number without ending the call.")]
        [ToolBehavior(ToolAction.Silent)]
        public async Task<FunctionReturn<AssistantSwitchResult>> SwitchAssistantAsync(
            string targetAssistantNumber,
            CancellationToken cancellationToken = default)
        {
            AssistantSwitchResult result = await this.CallControl.SwitchAssistantAsync(
                targetAssistantNumber,
                cancellationToken);

            return new FunctionReturn<AssistantSwitchResult>
            {
                Result = result,
                Next = ToolAction.Silent,
            };
        }
    }
}
