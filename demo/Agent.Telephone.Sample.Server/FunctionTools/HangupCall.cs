using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Sample.Server.FunctionTools
{
    public class HangupCall : PrivateFunctionTool
    {
        [Description("当来电者明确表示要结束当前通话时调用，例如“再见”“拜拜”“挂了吧”或“结束通话”。调用后会先向用户告别，播报完成后再挂断电话，不能用于普通告别、转接或其他操作。")]
        [ToolBehavior(ToolAction.Continue)]
        public FunctionReturn<string> HangupCurrentCall()
        {
            if (!this.CallControl.IsCallActive)
            {
                return new FunctionReturn<string>
                {
                    Result = "当前通话已结束。",
                    Next = ToolAction.Silent
                };
            }

            const string farewellInstruction = "用户已明确希望结束通话。请用一句自然、简短且不提出新问题的话向用户告别。";
            this.Logger.LogInformation("LLM 请求在告别播报完成后挂断当前通话。");
            this.CallControl.HangupCurrentCallAfterReply();
            return new FunctionReturn<string>
            {
                Result = farewellInstruction,
                Response = farewellInstruction,
                Next = ToolAction.Continue
            };
        }
    }
}
