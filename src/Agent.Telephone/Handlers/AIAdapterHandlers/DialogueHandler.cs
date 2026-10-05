using System.Threading.Channels;
using System.Collections.Concurrent;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class DialogueHandler : BaseHandler, IInAIAdapterHandler<string>, IOutAIAdapterHandler<OutSegment>, ILlmEventCallback
    {
        private ILlm? _llm;
        private IOfflineDialogue? _offlineDialogue;
        private readonly ConcurrentDictionary<long, DialogueTurnContext> _turnContexts = [];

        private readonly ObjectPool<Workflow<string>> _textWorkflowPool;
        private readonly ObjectPool<Workflow<OutSegment>> _segmentWorkflowPool;
        private readonly ObjectPool<OutSegment> _segmentPool;

        public DialogueHandler(
            ObjectPool<Workflow<string>> textWorkflowPool,
            ObjectPool<Workflow<OutSegment>> segmentWorkflowPool,
            ObjectPool<OutSegment> segmentPool,
            TelephoneConfig config,
            ILogger<DialogueHandler> logger)
            : base(config, logger)
        {
            this._textWorkflowPool = textWorkflowPool;
            this._segmentWorkflowPool = segmentWorkflowPool;
            this._segmentPool = segmentPool;
        }

        public override string HandlerName => HandlerNames.DialogueHandlerName;
        public ChannelReader<Workflow<string>> PreviousReader { get; set; } = null!;
        public ChannelWriter<Workflow<OutSegment>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            PrivateProvider privateProvider = this.ActiveCallContext.AIAgentContext.PrivateProvider;
            if (privateProvider.Llm is null || privateProvider.OfflineDialogue is null || privateProvider.DtmfInput is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 LLM、离线对话提供程序或 DTMF 输入提供程序。", this.ActiveCallContext.DeviceId);
                return false;
            }

            this._llm = privateProvider.Llm;
            this._offlineDialogue = privateProvider.OfflineDialogue;

            this._llm.RegisterDevice(this.ActiveCallContext, this);
            this._offlineDialogue.RegisterDevice(this.ActiveCallContext);

            this.RegisterCancellationToken(this.ActiveCallContext, continueAfterCallEnded: true);
            return true;
        }

        public async Task HandleAsync()
        {
            await foreach (Workflow<string> workflow in this.PreviousReader.ReadAllAsync())
            {
                try
                {
                    await this.HandleAsync(workflow);
                }
                finally
                {
                    this._textWorkflowPool.Return(workflow);
                }
            }
        }

        public async Task HandleAsync(Workflow<string> workflow)
        {
            if (!this.CheckWorkflowValid(workflow) || this._llm is null || this._offlineDialogue is null)
            {
                return;
            }
            if (!this.ActiveCallContext.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                return;
            }
            if (!this.ActiveCallContext.DeviceContext.TryBeginBackgroundReply(this.ActiveCallContext))
            {
                lease.Dispose();
                return;
            }

            using (lease)
            {
                bool contextAdded = false;
                try
                {
                    OfflineDialogueTurn turn = await this._offlineDialogue.BeginTurnAsync(
                        this.ActiveCallContext,
                        workflow.TurnId,
                        workflow.Data,
                        CancellationToken.None);
                    contextAdded = this._turnContexts.TryAdd(workflow.TurnId, new DialogueTurnContext(turn));
                    if (!contextAdded)
                    {
                        throw new InvalidOperationException($"Dialogue turn {workflow.TurnId} is already active.");
                    }

                    //await Task.Delay(10 * 1000);

                    await this._llm.StartDialogueAsync(workflow.TurnId, workflow.Data, this.HandlerToken);
                }
                catch (OperationCanceledException)
                {
                    if (contextAdded)
                    {
                        await this.OnCancelledAsync(workflow.TurnId, CancellationToken.None);
                    }
                    this.Logger.LogDebug("LLM 对话已取消，设备 {DeviceId}", this.ActiveCallContext.DeviceId);
                }
                catch (Exception exception)
                {
                    if (contextAdded)
                    {
                        await this.OnFailedAsync(workflow.TurnId, exception, CancellationToken.None);
                    }
                    this.Logger.LogError(exception, "处理来自设备的 LLM 对话失败: {deviceId}。", this.ActiveCallContext.DeviceId);
                }
                finally
                {
                    if (contextAdded)
                    {
                        this._turnContexts.TryRemove(workflow.TurnId, out _);
                    }
                    this.ActiveCallContext.DeviceContext.EndBackgroundReply();
                }
            }
        }

        public async void HandleSystemDialogueAsync(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                return;
            }

            Workflow<string> workflow = this._textWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, userMessage);
            try
            {
                await this.HandleAsync(workflow);
            }
            finally
            {
                this._textWorkflowPool.Return(workflow);
            }
        }

        private async Task<bool> QueueRealtimeSegmentAsync(OutSegment segment)
        {
            if (this.ActiveCallContext.CallToken.IsCancellationRequested)
            {
                return false;
            }

            OutSegment clonedSegment = this._segmentPool.Get();
            clonedSegment.Initialize(segment.Content, segment.IsFirstSegment, segment.IsLastSegment, segment.ParagraphId, segment.SentenceId);
            Workflow<OutSegment> workflow = this._segmentWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, clonedSegment);
            try
            {
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
                return true;
            }
            catch
            {
                this._segmentPool.Return(clonedSegment);
                this._segmentWorkflowPool.Return(workflow);
                return false;
            }
        }

        private async Task<bool> QueueDeferredHangupSegmentAsync(DialogueTurnContext context)
        {
            DeferredHangupSegment? deferredSegment = context.DeferredHangupSegment;
            if (deferredSegment is null)
            {
                return false;
            }

            OutSegment segment = this._segmentPool.Get();
            segment.Initialize(
                deferredSegment.Content,
                isFirst: true,
                isLast: true,
                deferredSegment.ParagraphId,
                deferredSegment.SentenceId);
            try
            {
                return await this.QueueRealtimeSegmentAsync(segment);
            }
            finally
            {
                this._segmentPool.Return(segment);
            }
        }

        private async Task EnsureOfflinePersistenceAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken)
        {
            if (turn.IsOfflineDelivery)
            {
                return;
            }

            if (!this.ActiveCallContext.CallToken.IsCancellationRequested && this.ActiveCallContext.UserAgent.IsCallActive)
            {
                return;
            }

            turn.IsOfflineDelivery = true;
            await this._offlineDialogue!.BeginOfflinePersistenceAsync(turn, cancellationToken);
        }

        #region LLM Event Callback
        public async Task OnBeforeFirstSegmentAsync(long turnId, OutSegment firstSegment, CancellationToken cancellationToken)
        {
            DialogueTurnContext context = this.GetTurnContext(turnId);
            await this.EnsureOfflinePersistenceAsync(context.Turn, cancellationToken);
        }

        public async Task OnSegmentAsync(long turnId, OutSegment segment, CancellationToken cancellationToken)
        {
            DialogueTurnContext context = this.GetTurnContext(turnId);
            OfflineDialogueTurn turn = context.Turn;
            await this.EnsureOfflinePersistenceAsync(turn, cancellationToken);
            await this._offlineDialogue!.AppendAssistantSegmentAsync(turn, segment.Content, null, cancellationToken);
            if (this.ActiveCallContext.IsHangupAfterReplyPending(turnId))
            {
                context.CaptureDeferredHangupSegment(segment);
                return;
            }

            if (!turn.IsOfflineDelivery)
            {
                if (await this.QueueRealtimeSegmentAsync(segment))
                {
                    Interlocked.Exchange(ref context.HasRealtimeSegment, 1);
                }
            }
        }

        public async Task OnToolExecutionPromptAsync(long turnId, OutSegment segment, CancellationToken cancellationToken)
        {
            DialogueTurnContext context = this.GetTurnContext(turnId);
            await this.EnsureOfflinePersistenceAsync(context.Turn, cancellationToken);
            if (!context.Turn.IsOfflineDelivery)
            {
                bool played = await this.ActiveCallContext.AIAgentContext.PlayToolExecutionPromptAsync(segment.Content, cancellationToken);
                this.Logger.LogDebug("工具执行前提示播放完成，TurnId {TurnId}，成功 {Played}。", turnId, played);
            }
        }

        public async Task OnCompletedAsync(long turnId, CancellationToken cancellationToken)
        {
            DialogueTurnContext context = this.GetTurnContext(turnId);
            OfflineDialogueTurn turn = context.Turn;
            await this.EnsureOfflinePersistenceAsync(turn, cancellationToken);
            if (!turn.IsOfflineDelivery &&
                !this.ActiveCallContext.DeviceContext.TryRecordCompletedOnlineTurn(this.ActiveCallContext, turn))
            {
                turn.IsOfflineDelivery = true;
                await this._offlineDialogue!.BeginOfflinePersistenceAsync(turn, cancellationToken);
            }
            if (!turn.IsOfflineDelivery &&
                this.ActiveCallContext.IsHangupAfterReplyPending(turnId) &&
                await this.QueueDeferredHangupSegmentAsync(context))
            {
                Interlocked.Exchange(ref context.HasRealtimeSegment, 1);
            }

            bool completed = await this.FinishAsync(
                context,
                token => this._offlineDialogue!.CompleteTurnAsync(
                    turn,
                    read: !turn.IsOfflineDelivery,
                    token),
                cancellationToken);
            if (Volatile.Read(ref context.HasRealtimeSegment) == 0)
            {
                this.ActiveCallContext.CompleteHangupAfterReply(turnId);
            }
            if (completed && turn.IsOfflineDelivery)
            {
                await this._offlineDialogue!.StartProactiveCallAsync(this.ActiveCallContext.DeviceContext, turn);
            }
        }

        public async Task OnCancelledAsync(long turnId, CancellationToken cancellationToken)
        {
            DialogueTurnContext context = this.GetTurnContext(turnId);
            this.ActiveCallContext.CompleteHangupAfterReply(turnId);
            await this.FinishAsync(
                context,
                token => this._offlineDialogue!.DiscardTurnAsync(context.Turn, token),
                cancellationToken);
        }

        public async Task OnFailedAsync(long turnId, Exception exception, CancellationToken cancellationToken)
        {
            DialogueTurnContext context = this.GetTurnContext(turnId);
            this.ActiveCallContext.CompleteHangupAfterReply(turnId);
            await this.FinishAsync(
                context,
                token => this._offlineDialogue!.FailTurnAsync(context.Turn, token),
                cancellationToken);
        }
        #endregion

        private DialogueTurnContext GetTurnContext(long turnId)
        {
            return this._turnContexts.TryGetValue(turnId, out DialogueTurnContext? context)
                ? context
                : throw new InvalidOperationException($"The LLM response sink has no active dialogue turn {turnId}.");
        }

        private async Task<bool> FinishAsync(
            DialogueTurnContext context,
            Func<CancellationToken, ValueTask> finishAsync,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref context.Terminal, 1) != 0)
            {
                return false;
            }

            try
            {
                await finishAsync(cancellationToken);
                await this._offlineDialogue!.FlushTurnAsync(context.Turn, CancellationToken.None);
                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "结算 LLM Turn {TurnId} 的持久化状态失败。", context.Turn.TurnId);
                return false;
            }
        }

        private sealed class DialogueTurnContext
        {
            private DeferredHangupSegment? _deferredHangupSegment;

            public DialogueTurnContext(OfflineDialogueTurn turn)
            {
                this.Turn = turn;
            }

            public OfflineDialogueTurn Turn { get; }
            public DeferredHangupSegment? DeferredHangupSegment => Volatile.Read(ref this._deferredHangupSegment);
            public int Terminal;
            public int HasRealtimeSegment;

            public void CaptureDeferredHangupSegment(OutSegment segment)
            {
                if (string.IsNullOrWhiteSpace(segment.Content))
                {
                    return;
                }

                DeferredHangupSegment? previous = this._deferredHangupSegment;
                string content = previous is null ? segment.Content : $"{previous.Content.TrimEnd('。')}。{segment.Content}";
                this._deferredHangupSegment = new DeferredHangupSegment(content, previous?.ParagraphId ?? segment.ParagraphId, previous?.SentenceId ?? segment.SentenceId);
            }
        }

        private sealed record DeferredHangupSegment(string Content, string? ParagraphId, string? SentenceId);

    }
}
