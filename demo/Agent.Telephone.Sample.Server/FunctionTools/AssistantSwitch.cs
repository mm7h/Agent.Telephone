using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;

namespace Agent.Telephone.Sample.Server.FunctionTools
{
    internal class AssistantSwitch : PrivateFunctionTool
    {
        [Description("在不结束当前电话的情况下，将来电切换到另一个专业 Assistant。仅当来电者明确需要其他专业服务时调用，不要用它代替当前 Assistant 直接回答问题。可转接的目标只有：10086（通用问答：日常咨询、常识和知识问答、问题分析、思路梳理、建议及轻松闲聊）；10085（写作与语言：文案起草、润色、改写、翻译、措辞优化，以及根据受众和场景调整表达）。调用时必须传入完整目标号码，不要传角色名称、用户电话号码、多个号码或自行编造的号码；不要转接到当前 Assistant。成功后由目标 Assistant 在同一通电话中继续服务。")]
        [ToolBehavior(ToolAction.Silent)]
        public async Task<FunctionReturn<AssistantSwitchResult>> SwitchAssistantAsync(
            [Description("要切换到的目标 Assistant 拨号号码。只能填写可转接列表中的完整号码：10086 或 10085；不要填写角色名称、职责描述、用户电话号码或多个号码。")]
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
