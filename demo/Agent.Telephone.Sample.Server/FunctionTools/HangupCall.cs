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
        private const string FAREWELL_MESSAGE = "感谢您的来电，再见。";

        [Description("当来电者明确表示要结束当前通话时调用，例如“再见”“拜拜”“挂了吧”或“结束通话”。调用后会先向用户告别，播报完成后再挂断电话，不能用于普通告别、转接或其他操作。")]
        [ToolBehavior(ToolAction.DirectResponse)]
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


            this.Logger.LogInformation("LLM 请求在告别播报完成后挂断当前通话。");
            this.CallControl.HangupCurrentCallAfterReply();
            return new FunctionReturn<string>
            {
                Result = FAREWELL_MESSAGE,
                Response = FAREWELL_MESSAGE,
                Next = ToolAction.DirectResponse
            };
        }
    }
}
