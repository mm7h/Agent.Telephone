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
        private const string ASSISTANT_SWITCH_DESCRIPTION = "在不结束当前电话的情况下，将来电切换到另一个专业 Assistant。仅当来电者明确需要其他专业服务时调用，不要用它代替当前 Assistant 直接回答问题。";
        private const string TARGET_ASSISTANT_NUMBER_DESCRIPTION = "必填。目标 Assistant 拨号号码：日常咨询、知识问答或轻松闲聊传入 \"10086\"；写作、润色、改写或翻译传入 \"10085\"。无法确定目标号码时不要调用此工具。";

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

        [Description(ASSISTANT_SWITCH_DESCRIPTION)]
        [ToolBehavior(ToolAction.Silent)]
        public async Task<FunctionReturn<AssistantSwitchResult>> SwitchAssistantAsync([Description(TARGET_ASSISTANT_NUMBER_DESCRIPTION)] string? targetAssistantNumber = null, CancellationToken cancellationToken = default)
        {
            this.Logger.LogInformation("开始尝试切换到目标 Assistant {TargetAssistantNumber}", targetAssistantNumber);

            if (string.IsNullOrWhiteSpace(targetAssistantNumber))
            {
                AssistantSwitchResult failedResult = new AssistantSwitchResult(AssistantSwitchStatus.Failed, string.Empty, "目标 Assistant 拨号号码不能为空。");
                return new FunctionReturn<AssistantSwitchResult>
                {
                    Result = failedResult,
                    Next = ToolAction.Continue,
                };
            }

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
