using Agent.Telephone.Abstractions.Common.Enums;
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
    internal sealed class Text2AudioHandler : BaseHandler, IInAIAdapterHandler<OutSegment>, IOutAIAdapterHandler<OutAudioSegment>, ITtsEventCallback
    {
        private ITts? _tts;
        private readonly ObjectPool<Workflow<OutSegment>> _segmentWorkflowPool;
        private readonly ObjectPool<OutSegment> _segmentPool;
        private readonly ObjectPool<Workflow<OutAudioSegment>> _audioWorkflowPool;
        private readonly ObjectPool<OutAudioSegment> _audioSegmentPool;
        private readonly SemaphoreSlim _synthesisLock = new(1, 1);
        private long _synthesizingTurnId = -1;
        private int _synthesisFailed;

        public Text2AudioHandler(ObjectPool<Workflow<OutSegment>> segmentWorkflowPool,
            ObjectPool<OutSegment> segmentPool,
            ObjectPool<Workflow<OutAudioSegment>> audioWorkflowPool,
            ObjectPool<OutAudioSegment> audioSegmentPool,
            TelephoneConfig config,
            ILogger<Text2AudioHandler> logger) : base(config, logger)
        {
            this._segmentPool = segmentPool;
            this._segmentWorkflowPool = segmentWorkflowPool;
            this._audioWorkflowPool = audioWorkflowPool;
            this._audioSegmentPool = audioSegmentPool;
        }

        public override string HandlerName => HandlerNames.Text2AudioHandlerName;
        public ChannelReader<Workflow<OutSegment>> PreviousReader { get; set; } = null!;
        public ChannelWriter<Workflow<OutAudioSegment>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            PrivateProvider privateProvider = this.ActiveCallContext.AIAgentContext.PrivateProvider;
            if (privateProvider.Tts is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 TTS 提供程序。", this.ActiveCallContext.DeviceId);
                return false;
            }

            this._tts = privateProvider.Tts;
            this._tts.RegisterDevice(this.ActiveCallContext, this);

            this.RegisterCancellationToken(this.ActiveCallContext, continueAfterCallEnded: true);
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
            bool synthesized = await this.SynthesizeAsync(workflow, trackGeneratedAudio: true, this.HandlerToken);
            if (!synthesized)
            {
                this.ActiveCallContext.CompleteHangupAfterReply(workflow.TurnId);
            }
        }

        public async Task<bool> SynthesizePromptAsync(
            string content,
            string paragraphId,
            string sentenceId,
            CancellationToken cancellationToken)
        {
            OutSegment segment = this._segmentPool.Get();
            Workflow<OutSegment> workflow = this._segmentWorkflowPool.Get();
            segment.Initialize(
                content,
                isFirst: true,
                isLast: true,
                paragraphId: paragraphId,
                sentenceId: sentenceId);
            workflow.Initialize(this.ActiveCallContext, segment);

            try
            {
                return await this.SynthesizeAsync(workflow, trackGeneratedAudio: false, cancellationToken);
            }
            finally
            {
                this._segmentPool.Return(segment);
                this._segmentWorkflowPool.Return(workflow);
            }
        }

        private async Task<bool> SynthesizeAsync(
            Workflow<OutSegment> workflow,
            bool trackGeneratedAudio,
            CancellationToken cancellationToken)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return false;
            }
            if (this._tts is null)
            {
                this.Logger.LogError("TTS提供程序未为设备配置: {deviceId}。", this.ActiveCallContext.DeviceId);
                return false;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(workflow.Data.Content))
                {
                    this.Logger.LogInformation("无需TTS，查询文本为空。");
                    return false;
                }
                using CancellationTokenSource synthesisCts = CancellationTokenSource.CreateLinkedTokenSource(
                    this.HandlerToken,
                    cancellationToken);
                CancellationToken synthesisToken = synthesisCts.Token;
                synthesisToken.ThrowIfCancellationRequested();
                await this._synthesisLock.WaitAsync(synthesisToken);
                try
                {
                    Interlocked.Exchange(ref this._synthesizingTurnId, workflow.TurnId);
                    Volatile.Write(ref this._synthesisFailed, 0);
                    await this._tts.SynthesisAsync(workflow, synthesisToken);
                    return Volatile.Read(ref this._synthesisFailed) == 0;
                }
                finally
                {
                    Interlocked.Exchange(ref this._synthesizingTurnId, -1);
                    this._synthesisLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("TTS 合成已取消，设备 {DeviceId}。", this.ActiveCallContext.DeviceId);
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "处理设备文本转语音时出错: {deviceId}。", this.ActiveCallContext.DeviceId);
                return false;
            }
        }
        public async void OnBeforeProcessing(string sentence, bool isFirstSegment, bool isLastSegment)
        {
            if (isFirstSegment)
            {
                OutAudioSegment outAudioSegment = this._audioSegmentPool.Get();
                Workflow<OutAudioSegment> nextWorkflow = this._audioWorkflowPool.Get();
                outAudioSegment.Initialize(audioType: AudioType.TTS, content: sentence, isFirstSegment: isFirstSegment, isLastSegment: isLastSegment);

                nextWorkflow.Initialize(this.ActiveCallContext, outAudioSegment);
                try
                {
                    await this.NextWriter.WriteAsync(nextWorkflow, this.HandlerToken);
                }
                catch (OperationCanceledException)
                {
                    this._audioSegmentPool.Return(outAudioSegment);
                    this._audioWorkflowPool.Return(nextWorkflow);
                }
                catch (ChannelClosedException)
                {
                    this._audioSegmentPool.Return(outAudioSegment);
                    this._audioWorkflowPool.Return(nextWorkflow);
                }
            }
            this.Logger.LogDebug("设备 {deviceId} TTS 处理句子: {sentence}.", this.ActiveCallContext.DeviceId, sentence);
        }
        public async void OnProcessing(float[] audioData, bool isFirstFrame, bool isLastFrame)
        {
            if (audioData.Length == 0 || this.HandlerToken.IsCancellationRequested)
            {
                return;
            }

            OutAudioSegment audioSegment = this._audioSegmentPool.Get();
            audioSegment.Initialize(audioType: AudioType.TTS, audioData: audioData, isFirstFrame: isFirstFrame, isLastFrame: isLastFrame);


            Workflow<OutAudioSegment> workflow = this._audioWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, audioSegment);
            try
            {
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this._audioSegmentPool.Return(audioSegment);
                this._audioWorkflowPool.Return(workflow);
            }
            catch
            {
                this._audioSegmentPool.Return(audioSegment);
                this._audioWorkflowPool.Return(workflow);
            }
        }



        public async void OnProcessed(string sentence, bool isFirstSegment, bool isLastSegment, TtsGenerateResult ttsGenerateResult)
        {
            if (ttsGenerateResult != TtsGenerateResult.Success)
            {
                if (Volatile.Read(ref this._synthesizingTurnId) >= 0)
                {
                    Volatile.Write(ref this._synthesisFailed, 1);
                }
                return;
            }

            if (isLastSegment)
            {
                OutAudioSegment finalSegment = this._audioSegmentPool.Get();
                finalSegment.Initialize(
                    audioType: AudioType.TTS,
                    isLastSegment: true,
                    isLastFrame: true);
                Workflow<OutAudioSegment> finalWorkflow = this._audioWorkflowPool.Get();
                finalWorkflow.Initialize(this.ActiveCallContext, finalSegment);
                try
                {
                    await this.NextWriter.WriteAsync(finalWorkflow, this.HandlerToken);
                }
                catch
                {
                    this._audioSegmentPool.Return(finalSegment);
                    this._audioWorkflowPool.Return(finalWorkflow);
                }
            }
        }

        public void OnSentenceStart(string sentence, string sentenceId)
        {
            this.QueueSubtitleMarker(sentence, sentenceId, isSentenceStart: true);
        }
        public void OnSentenceEnd(string sentence, string sentenceId)
        {
            this.QueueSubtitleMarker(sentence, sentenceId, isSentenceStart: false);
        }

        private void QueueSubtitleMarker(string sentence, string sentenceId, bool isSentenceStart)
        {
            if (this.HandlerToken.IsCancellationRequested || string.IsNullOrWhiteSpace(sentenceId))
            {
                return;
            }

            OutAudioSegment audioSegment = this._audioSegmentPool.Get();
            Workflow<OutAudioSegment> workflow = this._audioWorkflowPool.Get();
            audioSegment.Initialize(
                audioType: AudioType.TTS,
                content: sentence,
                isFirstFrame: isSentenceStart,
                isLastFrame: !isSentenceStart,
                sentenceId: $"{(isSentenceStart ? "S" : "E")}_{sentenceId}");
            workflow.Initialize(this.ActiveCallContext, audioSegment);
            try
            {
                this.NextWriter
                    .WriteAsync(workflow, this.HandlerToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                this._audioSegmentPool.Return(audioSegment);
                this._audioWorkflowPool.Return(workflow);
            }
        }

        public override void Dispose()
        {
            this._synthesisLock.Dispose();
            base.Dispose();
        }

    }
}
