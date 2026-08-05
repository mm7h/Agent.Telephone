using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.FunctionTools
{
    /// <summary>
    /// Lets the LLM offer a one-key telephone menu without accessing SIP/RTP objects.
    /// </summary>
    internal sealed class DtmfInputFunctionTool : PrivateFunctionTool
    {
        [Description("播报按键菜单提示并等待用户按下指定按键。仅可传入已标记给当前已授权功能的按键。")]
        [ToolBehavior(AllowIntentDetection = false)]
        public async Task<FunctionReturn<DtmfInputResult>> RequestDtmfInputAsync(
            string prompt,
            DtmfKey keys,
            CancellationToken cancellationToken = default)
        {
            DtmfInputResult result = await this.CallControl
                .RequestDtmfInputAsync(keys, cancellationToken)
                ;

            return new FunctionReturn<DtmfInputResult>
            {
                Result = result,
                Response = result.Succeeded ? prompt : result.Message,
            };
        }
    }
}
