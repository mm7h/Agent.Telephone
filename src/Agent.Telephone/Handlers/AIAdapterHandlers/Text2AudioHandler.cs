using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.TTS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class Text2AudioHandler : BaseHandler, IInAIAdapterHandler<OutSegment>, IOutAIAdapterHandler<float[]>, ITtsEventCallback
    {
        private ITts? _tts;
        private readonly ObjectPool<Workflow<OutSegment>> _segmentWorkflowPool;
        private readonly ObjectPool<OutSegment> _segmentPool;
        private readonly ObjectPool<Workflow<float[]>> _audioWorkflowPool;
        private readonly IOfflineDialogue _offlineDialogue;
        private readonly SemaphoreSlim _synthesisLock = new(1, 1);
        private long _activeTurnId;
        private PromptCapture? _promptCapture;

        public Text2AudioHandler(ObjectPool<Workflow<OutSegment>> segmentWorkflowPool,
            ObjectPool<OutSegment> segmentPool,
            ObjectPool<Workflow<float[]>> audioWorkflowPool,
            IOfflineDialogue offlineDialogue,
            TelephoneConfig config,
            ILogger<Text2AudioHandler> logger) : base(config, logger)
        {
            this._segmentPool = segmentPool;
            this._segmentWorkflowPool = segmentWorkflowPool;
            this._audioWorkflowPool = audioWorkflowPool;
            this._offlineDialogue = offlineDialogue;
        }

        public override string HandlerName => HandlerNames.Text2AudioHandlerName;
        public ChannelReader<Workflow<OutSegment>> PreviousReader { get; set; } = null!;
        public ChannelWriter<Workflow<float[]>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", this.DeviceContext.DeviceId);
                return false;
            }
            PrivateProvider privateProvider = this.DeviceContext.ActiveCall.AIAgentContext.PrivateProvider;
            if (privateProvider.Tts is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 TTS 提供程序。", this.DeviceContext.DeviceId);
                return false;
            }

            this._tts = privateProvider.Tts;
            this._tts.RegisterDevice(this.DeviceContext.DeviceId, this);

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
                    this._segmentPool.Return(workflow.Data);
                    this._segmentWorkflowPool.Return(workflow);
                }
            }
        }
        public async Task HandleAsync(Workflow<OutSegment> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }
            if (this._tts is null)
            {
                this.Logger.LogError("TTS提供程序未为设备配置: {deviceId}。", this.DeviceContext.DeviceId);
                return;
            }
            try
            {
                this._activeTurnId = workflow.TurnId;
                if (string.IsNullOrWhiteSpace(workflow.Data.Content))
                {
                    this.Logger.LogInformation("无需TTS，查询文本为空。");
                    return;
                }
                this.HandlerToken.ThrowIfCancellationRequested();
                await this._synthesisLock.WaitAsync(this.HandlerToken);
                try
                {
                    await this._tts.SynthesisAsync(workflow, this.HandlerToken);
                    if (!string.IsNullOrWhiteSpace(workflow.Data.SentenceId))
                    {
                        await this._offlineDialogue.TrackGeneratedAudioAsync(
                            this.ActiveCallContext,
                            workflow.TurnId,
                            workflow.Data.SentenceId,
                            workflow.Data.IsLastSegment,
                            this.HandlerToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    this._synthesisLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("TTS 合成已取消，设备 {DeviceId}。", this.DeviceContext.DeviceId);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "处理设备文本转语音时出错: {deviceId}。", this.DeviceContext.DeviceId);
            }
        }
        public void OnBeforeProcessing(string sentence, bool isFirstSegment, bool isLastSegment)
        {
        }
        public void OnProcessing(float[] audioData, bool isFirstFrame, bool isLastFrame)
        {
            if (audioData.Length == 0 || this.HandlerToken.IsCancellationRequested)
            {
                return;
            }

            PromptCapture? promptCapture = this._promptCapture;
            if (promptCapture is not null)
            {
                promptCapture.Append(audioData);
                return;
            }

            Workflow<float[]> workflow = this._audioWorkflowPool.Get();
            workflow.Initialize(
                this.ActiveCallContext,
                audioData,
                isFinal: false);
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
                this._audioWorkflowPool.Return(workflow);
            }
            catch
            {
                this._audioWorkflowPool.Return(workflow);
            }
        }



        public void OnProcessed(string sentence, bool isFirstSegment, bool isLastSegment, TtsGenerateResult ttsGenerateResult)
        {
            PromptCapture? promptCapture = this._promptCapture;
            if (promptCapture is not null)
            {
                if (ttsGenerateResult == TtsGenerateResult.Failed)
                {
                    promptCapture.Fail(new InvalidOperationException("TTS prompt synthesis failed."));
                }
                else if (isLastSegment)
                {
                    promptCapture.Complete();
                }
                return;
            }

            if (isLastSegment && ttsGenerateResult == TtsGenerateResult.Success)
            {
                Workflow<float[]> finalWorkflow = this._audioWorkflowPool.Get();
                finalWorkflow.Initialize(
                    this.ActiveCallContext,
                    Array.Empty<float>(),
                    isFinal: true);
                try
                {
                    this.NextWriter
                        .WriteAsync(finalWorkflow, this.HandlerToken)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    this._audioWorkflowPool.Return(finalWorkflow);
                    _ = this._offlineDialogue.MarkTurnPlaybackCompletedAsync(
                        this.ActiveCallContext,
                        this._activeTurnId,
                        fullyPlayed: false,
                        CancellationToken.None);
                }
            }
        }

        public async Task<float[]> SynthesizePromptAsync(
            string text,
            CancellationToken cancellationToken)
        {
            if (this._tts is null || string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            await this._synthesisLock.WaitAsync(cancellationToken);
            PromptCapture capture = new();
            this._promptCapture = capture;
            try
            {
                OutSegment segment = new();
                segment.Initialize(text, isFirst: true, isLast: true);
                Workflow<OutSegment> workflow = new();
                workflow.Initialize(this.ActiveCallContext, segment);
                await this._tts.SynthesisAsync(workflow, cancellationToken);
                return await capture.Completion.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                this._promptCapture = null;
                this._synthesisLock.Release();
            }
        }
        public void OnSentenceStart(string sentence, string sentenceId)
        {

        }
        public void OnSentenceEnd(string sentence, string sentenceId)
        {

        }

        public override void Dispose()
        {
            if (this._tts is not null)
            {
                this._tts.UnregisterDevice(this.DeviceContext.DeviceId);
            }
            this.NextWriter?.TryComplete();
            this._synthesisLock.Dispose();
            base.Dispose();
        }

        private sealed class PromptCapture
        {
            private readonly object _sync = new();
            private readonly List<float> _audio = [];

            public TaskCompletionSource<float[]> Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public void Append(ReadOnlySpan<float> audio)
            {
                lock (this._sync)
                {
                    for (int index = 0; index < audio.Length; index++)
                    {
                        this._audio.Add(audio[index]);
                    }
                }
            }

            public void Complete()
            {
                lock (this._sync)
                {
                    this.Completion.TrySetResult(this._audio.ToArray());
                }
            }

            public void Fail(Exception exception)
            {
                this.Completion.TrySetException(exception);
            }
        }
    }
}
