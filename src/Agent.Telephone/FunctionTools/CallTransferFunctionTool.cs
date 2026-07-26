using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;

namespace Agent.Telephone.FunctionTools
{
    internal sealed class CallTransferFunctionTool : PrivateFunctionTool
    {
        [Description("将当前电话静默转接到本服务中已注册且空闲的目标号码。")]
        [ToolBehavior(ToolAction.Silent)]
        public async Task<FunctionReturn<CallTransferResult>> TransferAsync(
            string targetNumber,
            CancellationToken cancellationToken = default)
        {
            CallTransferResult result = await this.CallControl.TransferAsync(
                targetNumber,
                cancellationToken);

            return new FunctionReturn<CallTransferResult>
            {
                Result = result,
                Next = ToolAction.Silent,
            };
        }
    }
}
