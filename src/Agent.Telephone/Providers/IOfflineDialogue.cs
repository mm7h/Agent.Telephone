using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers
{
    internal interface IOfflineDialogue : IProvider<ModelSetting>
    {
        /// <summary>
        /// 为当前通话创建一个离线对话 Turn。
        /// </summary>
        ValueTask<OfflineDialogueTurn> BeginTurnAsync(ActiveCallContext activeCall, long turnId, string userText, CancellationToken cancellationToken);

        /// <summary>
        /// 开始持久化离线对话 Turn。
        /// </summary>
        ValueTask BeginOfflinePersistenceAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken);

        /// <summary>
        /// 向离线对话 Turn 追加助手回复片段。
        /// </summary>
        ValueTask AppendAssistantSegmentAsync(OfflineDialogueTurn turn, string textContent, string? audioPath, CancellationToken cancellationToken);

        /// <summary>
        /// 将离线对话 Turn 标记为已完成。
        /// </summary>
        ValueTask CompleteTurnAsync(OfflineDialogueTurn turn, bool read, CancellationToken cancellationToken);

        /// <summary>
        /// 将离线对话 Turn 标记为失败。
        /// </summary>
        ValueTask FailTurnAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken);

        /// <summary>
        /// 丢弃离线对话 Turn。
        /// </summary>
        ValueTask DiscardTurnAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken);

        /// <summary>
        /// 等待离线对话 Turn 的持久化操作完成。
        /// </summary>
        Task FlushTurnAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken);

        /// <summary>
        /// 持久化当前通话中已完成的在线对话 Turn。
        /// </summary>
        Task PersistCompletedOnlineTurnsAsync(ActiveCallContext activeCall, CancellationToken cancellationToken);

        /// <summary>
        /// 为离线对话 Turn 发起主动回拨。
        /// </summary>
        Task StartProactiveCallAsync(DeviceContext device, OfflineDialogueTurn turn);

        /// <summary>
        /// 等待主动回拨的结果。
        /// </summary>
        Task WaitForProactiveCallResultAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken);

        /// <summary>
        /// 停止离线对话 Turn 的主动回拨。
        /// </summary>
        Task StopProactiveCallAsync(OfflineDialogueTurn turn);

        /// <summary>
        /// 在当前通话中播放指定的助手留言。
        /// </summary>
        Task PlayAssistantMessageAsync(ActiveCallContext activeCall, string messageId, Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt, CancellationToken cancellationToken);

        /// <summary>
        /// 启动通话接通后的初始离线对话流程。
        /// </summary>
        Task StartInitialCallFlowAsync(ActiveCallContext activeCall, Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt, CancellationToken cancellationToken);
    }
}
