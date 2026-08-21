using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.FunctionTools
{
    /// <summary>
    /// 通话控制能力
    /// </summary>
    public interface IAssistantControl
    {
        /// <summary>
        /// 获取当前来电号码。
        /// </summary>
        string? CallerNumber { get; }

        /// <summary>
        /// 获取当前通话绑定的助手号码。
        /// </summary>
        string? AssistantNumber { get; }

        /// <summary>
        /// 获取当前通话是否仍处于活跃状态。
        /// </summary>
        bool IsCallActive { get; }

        /// <summary>
        /// 在不挂断当前通话的情况下，切换到指定号码的助手。
        /// </summary>
        /// <param name="targetAssistantNumber">要切换到的目标 Assistant 拨号号码。</param>
        /// <param name="cancellationToken"></param>
        /// <returns>助手切换请求的结果。</returns>
        Task<AssistantSwitchResult> SwitchAssistantAsync(string targetAssistantNumber, CancellationToken cancellationToken = default);

        /// <summary>
        /// 挂断当前通话。
        /// </summary>
        void HangupCurrentCall();

        /// <summary>
        /// 为当前通话启动一次单键 DTMF 选择窗口。
        /// 该异步调用会等待用户按下指定按键之一、等待超时或被取消；它不会阻塞线程。
        /// 可使用 <see cref="DtmfKey.Zero"/> 至 <see cref="DtmfKey.Nine"/>、
        /// <see cref="DtmfKey.Star"/> 和 <see cref="DtmfKey.Pound"/> 的任意组合。
        /// </summary>
        Task<DtmfInputResult> RequestDtmfInputAsync(DtmfKey keys, CancellationToken cancellationToken = default);
    }
}
