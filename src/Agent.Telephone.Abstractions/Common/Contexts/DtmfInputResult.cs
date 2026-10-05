using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.Common.Contexts
{
    /// <summary>
    /// 请求活动呼叫接受 DTMF 菜单选择的结果。
    /// </summary>
    public sealed record DtmfInputResult(DtmfInputStatus Status, string? Message = null)
    {
        /// <summary>
        /// 获取被接受的电话号码键。仅当 <see cref="Status"/>
        /// 为 <see cref="DtmfInputStatus.Accepted"/> 时设置。
        /// </summary>
        public DtmfKey? SelectedKey { get; init; }

        public bool Succeeded => this.Status == DtmfInputStatus.Accepted && this.SelectedKey is not null;
    }
}
