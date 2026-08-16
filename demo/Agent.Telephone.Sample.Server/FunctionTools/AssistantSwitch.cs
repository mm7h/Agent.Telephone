using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Sample.Server.FunctionTools
{
    public class AssistantSwitch : PrivateFunctionTool
    {
        public override ValueTask OnFunctionToolInitializedAsync()
        {
            this.Logger.LogInformation("AssistantSwitch function tool initialized.");
            return base.OnFunctionToolInitializedAsync();
        }
        public override ValueTask OnFunctionToolReleasedAsync()
        {
            this.Logger.LogInformation("AssistantSwitch function tool released.");
            return base.OnFunctionToolReleasedAsync();
        }
        public override ValueTask OnDeviceConnectedAsync()
        {
            this.Logger.LogInformation("Session connected to AssistantSwitch function tool.");
            return base.OnDeviceConnectedAsync();
        }
        public override ValueTask OnDeviceClosedAsync()
        {
            this.Logger.LogInformation("Session closed from AssistantSwitch function tool.");
            return base.OnDeviceClosedAsync();
        }

        [Description("根据 Assistant 号码切换的方法工具。在不结束当前电话的情况下，将来电切换到另一个专业 Assistant。仅当来电者明确需要其他专业服务时调用，不要用它代替当前 Assistant 直接回答问题。")]
        [ToolBehavior(ToolAction.Silent)]
        public async Task<FunctionReturn<AssistantSwitchResult>> SwitchAssistantAsync(
            [Description("要切换到的目标 Assistant 拨号号码。")]
            string targetAssistantNumber,
            CancellationToken cancellationToken = default)
        {
            this.Logger.LogInformation("开始尝试切换到目标 Assistant {TargetAssistantNumber}", targetAssistantNumber);

            AssistantSwitchResult result = await this.CallControl.SwitchAssistantAsync(targetAssistantNumber, cancellationToken);

            this.Logger.LogInformation("切换到目标 Assistant {TargetAssistantNumber} 结果：{Result}", targetAssistantNumber, result);

            return new FunctionReturn<AssistantSwitchResult>
            {
                Result = result,
                Next = ToolAction.Silent,
            };
        }
    }
}
