using System.Threading.Channels;
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
        private OfflineDialogueTurn? _turn;
        private int _terminal;

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
                OfflineDialogueTurn? turn = null;
                try
                {
                    this._turn = null;
                    Volatile.Write(ref this._terminal, 0);
                    turn = await this._offlineDialogue.BeginTurnAsync(
                        this.ActiveCallContext,
                        workflow.TurnId,
                        workflow.Data,
                        CancellationToken.None);
                    this._turn = turn;

                    //await Task.Delay(10 * 1000);

                    await this._llm.StartDialogueAsync(workflow.Data, this.HandlerToken);
                }
                catch (OperationCanceledException)
                {
                    if (turn is not null)
                    {
                        await this.OnCancelledAsync(CancellationToken.None);
                    }
                    this.Logger.LogDebug("LLM 对话已取消，设备 {DeviceId}", this.ActiveCallContext.DeviceId);
                }
                catch (Exception exception)
                {
                    if (turn is not null)
                    {
                        await this.OnFailedAsync(exception, CancellationToken.None);
                    }
                    this.Logger.LogError(exception, "处理来自设备的 LLM 对话失败: {deviceId}。", this.ActiveCallContext.DeviceId);
                }
                finally
                {
                    if (ReferenceEquals(this._turn, turn))
                    {
                        this._turn = null;
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

        private async Task QueueRealtimeSegmentAsync(OutSegment segment)
        {
            if (this.ActiveCallContext.CallToken.IsCancellationRequested)
            {
                return;
            }

            OutSegment clonedSegment = this._segmentPool.Get();
            clonedSegment.Initialize(segment.Content, segment.IsFirstSegment, segment.IsLastSegment, segment.ParagraphId, segment.SentenceId);
            Workflow<OutSegment> workflow = this._segmentWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, clonedSegment);
            try
            {
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
            }
            catch
            {
                this._segmentPool.Return(clonedSegment);
                this._segmentWorkflowPool.Return(workflow);
            }
        }

        #region LLM Event Callback
        public async Task OnBeforeFirstSegmentAsync(OutSegment firstSegment, CancellationToken cancellationToken)
        {
            OfflineDialogueTurn turn = this.GetCurrentTurn();
            await this.EnsureOfflinePersistenceAsync(turn, cancellationToken);
        }

        public async Task OnSegmentAsync(OutSegment segment, CancellationToken cancellationToken)
        {
            OfflineDialogueTurn turn = this.GetCurrentTurn();
            await this.EnsureOfflinePersistenceAsync(turn, cancellationToken);
            await this._offlineDialogue!.AppendAssistantSegmentAsync(turn, segment.Content, null, cancellationToken);
            if (!turn.IsOfflineDelivery)
            {
                await this.QueueRealtimeSegmentAsync(segment);
            }
        }

        public async Task OnCompletedAsync(CancellationToken cancellationToken)
        {
            OfflineDialogueTurn turn = this.GetCurrentTurn();
            await this.EnsureOfflinePersistenceAsync(turn, cancellationToken);
            if (!turn.IsOfflineDelivery &&
                !this.ActiveCallContext.DeviceContext.TryRecordCompletedOnlineTurn(this.ActiveCallContext, turn))
            {
                turn.IsOfflineDelivery = true;
                await this._offlineDialogue!.BeginOfflinePersistenceAsync(turn, cancellationToken);
            }
            bool completed = await this.FinishAsync(
                turn,
                token => this._offlineDialogue!.CompleteTurnAsync(
                    turn,
                    read: !turn.IsOfflineDelivery,
                    token),
                cancellationToken);
            if (completed && turn.IsOfflineDelivery)
            {
                await this._offlineDialogue!.StartProactiveCallAsync(this.ActiveCallContext.DeviceContext, turn);
            }
        }

        public async Task OnCancelledAsync(CancellationToken cancellationToken)
        {
            OfflineDialogueTurn turn = this.GetCurrentTurn();
            await this.FinishAsync(
                turn,
                token => this._offlineDialogue!.DiscardTurnAsync(turn, token),
                cancellationToken);
        }

        public async Task OnFailedAsync(Exception exception, CancellationToken cancellationToken)
        {
            OfflineDialogueTurn turn = this.GetCurrentTurn();
            await this.FinishAsync(
                turn,
                token => this._offlineDialogue!.FailTurnAsync(turn, token),
                cancellationToken);
        }
        #endregion

        private OfflineDialogueTurn GetCurrentTurn() => this._turn
            ?? throw new InvalidOperationException("The LLM response sink has no active dialogue turn.");

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

        private async Task<bool> FinishAsync(
            OfflineDialogueTurn turn,
            Func<CancellationToken, ValueTask> finishAsync,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this._terminal, 1) != 0)
            {
                return false;
            }

            try
            {
                await finishAsync(cancellationToken);
                await this._offlineDialogue!.FlushTurnAsync(turn, CancellationToken.None);
                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "结算 LLM Turn {TurnId} 的持久化状态失败。", turn.TurnId);
                return false;
            }
        }

        public override void Dispose()
        {
            if (this._llm is not null)
            {
                this._llm.UnregisterDevice(this.ActiveCallContext);
            }
            if (this._offlineDialogue is not null)
            {
                this._offlineDialogue.UnregisterDevice(this.ActiveCallContext);
            }
            base.Dispose();
        }
    }
}
