using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.Common.Attributes
{
    /// <summary>
    /// 声明函数工具的默认行为。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class ToolBehaviorAttribute : Attribute
    {
        public ToolBehaviorAttribute()
        {
        }

        public ToolBehaviorAttribute(ToolAction defaultAction)
        {
            this.DefaultAction = defaultAction;
        }

        /// <summary>
        /// 当函数返回未指定 Next 时的默认动作。
        /// </summary>
        public ToolAction DefaultAction { get; set; } = ToolAction.Continue;

        /// <summary>
        /// 从当前活动的双音多频（DTMF）菜单中选择此功能的按键。
        /// 多个键可以使用按位或运算符进行组合。
        /// </summary>
        public DtmfKey DtmfKeys { get; set; } = DtmfKey.None;

        /// <summary>
        /// 调用 DTMF 工具前由系统播放的固定按键提示。
        /// </summary>
        public string? DtmfPrompt { get; set; }

        /// <summary>
        /// 调用工具前由系统立即播报的固定提示。
        /// </summary>
        public string? PreExecutionPrompt { get; set; }
    }
}
