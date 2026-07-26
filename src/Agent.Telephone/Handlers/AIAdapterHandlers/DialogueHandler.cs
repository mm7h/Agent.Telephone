using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.Conversation;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class DialogueHandler : BaseHandler, IInAIAdapterHandler<string>, IOutAIAdapterHandler<OutSegment>
    {
        private ILlm? _llm;
        private readonly ObjectPool<Workflow<string>> _textWorkflowPool;
        private readonly ObjectPool<Workflow<OutSegment>> _segmentWorkflowPool;
        private readonly ObjectPool<OutSegment> _segmentPool;
        private readonly ConversationProvider _conversationProvider;
        private long _activeTurnId;

        public DialogueHandler(
            ObjectPool<Workflow<string>> textWorkflowPool,
            ObjectPool<Workflow<OutSegment>> segmentWorkflowPool,
            ObjectPool<OutSegment> segmentPool,
            ConversationProvider conversationProvider,
            TelephoneConfig config,
            ILogger<DialogueHandler> logger) : base(config, logger)
        {
            this._textWorkflowPool = textWorkflowPool;
            this._segmentWorkflowPool = segmentWorkflowPool;
            this._segmentPool = segmentPool;
            this._conversationProvider = conversationProvider;
        }

        public override string HandlerName => HandlerNames.DialogueHandlerName;
        public ChannelReader<Workflow<string>> PreviousReader { get; set; } = null!;
        public ChannelWriter<Workflow<OutSegment>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", this.DeviceContext.DeviceId);
                return false;
            }
            PrivateProvider privateProvider = this.DeviceContext.ActiveCall.AIAgentContext.PrivateProvider;
            if (privateProvider.Llm is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 LLM 提供程序。", this.DeviceContext.DeviceId);
                return false;
            }
            this._llm = privateProvider.Llm;
            this._llm.RegisterDevice(this.DeviceContext.DeviceId);
            this._llm.OnBeforeTokenGenerate += this.OnBeforeTokenGenerate;
            this._llm.OnTokenGenerating += this.OnTokenGenerating;
            this._llm.OnTokenGenerated += this.OnTokenGenerated;

            this.RegisterCancellationToken(this.DeviceContext, continueAfterCallEnded: true);
            return true;
        }

        public async Task HandleAsync()
        {
            await foreach (var workflow in this.PreviousReader.ReadAllAsync())
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
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }

            if (this._llm is null)
            {
                this.Logger.LogError("LLM提供程序未为设备配置: {deviceId}。", this.DeviceContext.DeviceId);
                return;
            }

            try
            {
                this._activeTurnId = workflow.TurnId;
                await this._conversationProvider
                    .BeginAsync(this.ActiveCallContext, workflow.Data, this.HandlerToken)
                    .ConfigureAwait(false);
                await this._llm.StartDialogueAsync(workflow.Data, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("LLM 对话已取消，设备 {DeviceId}", this.DeviceContext.DeviceId);
            }
            catch (Exception exception)
            {
                await this._conversationProvider
                    .FailAsync(
                        this.ActiveCallContext,
                        workflow.TurnId,
                        exception.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                this.Logger.LogError(exception, "处理来自设备的LLM对话失败: {deviceId}。", this.DeviceContext.DeviceId);
            }
        }
        private void OnBeforeTokenGenerate()
        {
            // todo：可以做一些检查状态，做提前准备
        }
        private void OnTokenGenerating(OutSegment segment)
        {
            if (this.HandlerToken.IsCancellationRequested)
            {
                return;
            }
            this._conversationProvider.AppendResponse(
                this.ActiveCallContext,
                this._activeTurnId,
                segment.Content);
            OutSegment clonedSegment = this._segmentPool.Get();
            clonedSegment.Initialize(segment.Content, segment.IsFirstSegment, segment.IsLastSegment, segment.ParagraphId, segment.SentenceId);

            Workflow<OutSegment> workflow = this._segmentWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, clonedSegment);

            // todo：检查状态，如果客户端是挂机状态，这时候就可以开始呼叫客户端了

            try
            {
                this.NextWriter
                    .WriteAsync(workflow, this.HandlerToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                this._segmentPool.Return(clonedSegment);
                this._segmentWorkflowPool.Return(workflow);
            }
            catch
            {
                this._segmentPool.Return(clonedSegment);
                this._segmentWorkflowPool.Return(workflow);
            }
        }


        private void OnTokenGenerated(IEnumerable<OutSegment> segments)
        {
            bool hasOutput = false;
            foreach (OutSegment segment in segments)
            {
                hasOutput = true;
                this._segmentPool.Return(segment);
            }
            if (!hasOutput)
            {
                _ = this._conversationProvider.CompleteWithoutAudioAsync(
                    this.ActiveCallContext,
                    this._activeTurnId,
                    CancellationToken.None);
            }

            // todo：开始计时，如果超过30s没有接听，那么就取消呼叫
            // this.Config.SIPConfig.HangUpTimeoutSeconds
        }

        public override void Dispose()
        {
            if (this._llm is not null)
            {
                this._llm.OnBeforeTokenGenerate -= this.OnBeforeTokenGenerate;
                this._llm.OnTokenGenerating -= this.OnTokenGenerating;
                this._llm.OnTokenGenerated -= this.OnTokenGenerated;
                this._llm.UnregisterDevice(this.DeviceContext.DeviceId);
            }
            this.NextWriter?.TryComplete();
            base.Dispose();
        }
    }
}
