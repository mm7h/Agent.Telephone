using Microsoft.Extensions.Logging;
using System.ComponentModel;
using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;

namespace Agent.Telephone.Sample.Server.FunctionTools
{
    public class GetTime : FunctionTool
    {
        public override ValueTask OnFunctionToolInitializedAsync()
        {
            this.Logger.LogInformation("GetTime function tool initialized.");
            return ValueTask.CompletedTask;
        }

        public override ValueTask OnFunctionToolReleasedAsync()
        {
            this.Logger.LogInformation("GetTime function tool released.");
            return ValueTask.CompletedTask;
        }

        [Description("获取当前的日期和时间")]
        [ToolBehavior(ToolAction.DirectResponse)]
        public FunctionReturn<DateTime> GetNowTime()
        {
            DateTime now = DateTime.Now;
            return new FunctionReturn<DateTime>
            {
                Result = now,
                Response = $"现在时间是 {now:yyyy-MM-dd HH:mm:ss}"
            };
        }
    }
}
