using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.ASR.Contexts;
using Agent.Telephone.Providers.VAD;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class AudioReceivedHandler : BaseHandler, IInAIAdapterHandler<byte[]>, IOutAIAdapterHandler<float[]>, IVadEventCallback
    {
        private const int MinimumSpeechDurationMilliseconds = 500;

        private IAudioProcessor? _audioProcessor;
        private IAsr? _asr;
        private IVad? _vad;
        private readonly object _streamingQueueGate = new();
        private readonly ObjectPool<Workflow<byte[]>> _rtpPacketWorkflowPool;
        private readonly ObjectPool<Workflow<float[]>> _audioWorkflowPool;
        private Task _streamingOperationTail = Task.CompletedTask;
        private bool _streamingUtteranceActive;
        private bool _streamingUtteranceFailed;
        private long _streamingUtteranceTurnId = -1;
        private int _maxQueuedStreamingAudioFrames = 1;
        private int _queuedStreamingAudioFrames;

        public AudioReceivedHandler(
            ObjectPool<Workflow<byte[]>> rtpPacketWorkflowPool,
            ObjectPool<Workflow<float[]>> audioWorkflowPool,
            TelephoneConfig config,
            ILogger<AudioReceivedHandler> logger) : base(config, logger)
        {
            this._rtpPacketWorkflowPool = rtpPacketWorkflowPool;
            this._audioWorkflowPool = audioWorkflowPool;
        }

        public override string HandlerName => HandlerNames.AudioReceivedHandlerName;
        public ChannelReader<Workflow<byte[]>> PreviousReader { get; set; } = null!;
        public ChannelWriter<Workflow<float[]>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            PrivateProvider privateProvider = this.ActiveCallContext.AIAgentContext.PrivateProvider;
            if (privateProvider.Vad is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 Vad 提供程序。", this.ActiveCallContext.DeviceId);
                return false;
            }
            if (privateProvider.AudioProcessor is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 AudioProcessor 提供程序。", this.ActiveCallContext.DeviceId);
                return false;
            }

            this._audioProcessor = privateProvider.AudioProcessor;
            this._asr = privateProvider.Asr;
            this._vad = privateProvider.Vad;
            this._vad.RegisterDevice(this.ActiveCallContext, this);
            this._audioProcessor.RegisterDevice(this.ActiveCallContext);

            this._maxQueuedStreamingAudioFrames = Math.Max(
                1,
                AudioProcessSettings.StreamingAsrMaxQueuedAudioMilliseconds
                    / Math.Max(1, this.ActiveCallContext.PacketTimeMs));

            this.RegisterCancellationToken(this.ActiveCallContext);
            return true;
        }

        public async Task HandleAsync()
        {
            try
            {
                await foreach (var workflow in this.PreviousReader.ReadAllAsync())
                {
                    try
                    {
                        await this.HandleAsync(workflow);
                    }
                    finally
                    {
                        this._rtpPacketWorkflowPool.Return(workflow);
                    }
                }
            }
            finally
            {
                Task streamingOperations;
                lock (this._streamingQueueGate)
                {
                    streamingOperations = this._streamingOperationTail;
                }
                try
                {
                    await streamingOperations;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        public async Task HandleAsync(Workflow<byte[]> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }

            if (this.ActiveCallContext.IsUserAudioProcessing)
            {
                return;
            }


            if (this._vad is null)
            {
                this.Logger.LogError("VAD提供程序未为设备配置: {deviceId}。", this.ActiveCallContext.DeviceId);
                return;
            }
            if (this._audioProcessor is null)
            {
                this.Logger.LogError("音频解码处理器未为设备配置: {deviceId}。", this.ActiveCallContext.DeviceId);
                return;
            }

            try
            {
                float[] pcmData = await this._audioProcessor.DecodeAsync(workflow.Data, this.ActiveCallContext.NegotiatedAudioFormat, this.HandlerToken);

                this.HandlerToken.ThrowIfCancellationRequested();

                this.ActiveCallContext.DeviceContext.AudioInPacket.PushAudio(pcmData);

                if (this._asr?.IsStreaming == true)
                {
                    this.HandleStreamingAudio(pcmData);
                }

                await this._vad.AnalysisVoiceAsync(
                    this.ActiveCallContext.DeviceId,
                    pcmData,
                    this.ActiveCallContext.DeviceContext.AudioInPacket.GetAllAudio(),
                    this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("音频处理已取消，设备 {DeviceId}", this.ActiveCallContext.DeviceId);
            }
            catch (Exception ex)
            {
                this.ActiveCallContext.DeviceContext.AudioInPacket.Reset();
                this.Logger.LogError(ex, "处理来自设备的音频数据包失败: {deviceId}。", this.ActiveCallContext.DeviceId);
            }
        }

        public void OnVoiceDetected(float[] audioData)
        {
            ActiveCallContext activeCall = this.ActiveCallContext;
            activeCall.DeviceContext.AudioInPacket.ResetAudioBuffer();
            if (activeCall.IsUserAudioInputPaused || activeCall.IsUserAudioProcessing || this.HandlerToken.IsCancellationRequested)
            {
                return;
            }
            if (audioData.Length < AudioProcessSettings.OutputToModelSampleRate * MinimumSpeechDurationMilliseconds / 1000)
            {
                this.Logger.LogDebug("设备 {deviceId} 的语音太短。", this.ActiveCallContext.DeviceId);
                if (this._asr?.IsStreaming == true)
                {
                    this.AbortStreamingUtterance();
                }
                return;
            }

            if (this._asr?.IsStreaming == true &&
                (this._streamingUtteranceActive || this._streamingUtteranceFailed))
            {
                if (this._streamingUtteranceFailed)
                {
                    this.AbortStreamingUtterance();
                    return;
                }

                long streamingTurnId = this._streamingUtteranceTurnId;
                if (streamingTurnId != activeCall.TurnId || !this.FinishStreamingUtterance())
                {
                    this.AbortStreamingUtterance();
                    return;
                }

                activeCall.BeginUserAudioProcessing(streamingTurnId);
                return;
            }

            long turnId = activeCall.TurnId;
            Workflow<float[]> workflow = this._audioWorkflowPool.Get();
            workflow.Initialize(activeCall, audioData);
            activeCall.BeginUserAudioProcessing(turnId);
            _ = this.QueueVoiceDetectedAsync(workflow, turnId);
        }

        public void OnVoiceStarted()
        {
            if (this._asr?.IsStreaming != true ||
                this.ActiveCallContext.IsUserAudioInputPaused ||
                this.ActiveCallContext.IsUserAudioProcessing ||
                this.HandlerToken.IsCancellationRequested)
            {
                return;
            }

            this.StartStreamingUtterance();
        }

        private async Task QueueVoiceDetectedAsync(Workflow<float[]> workflow, long turnId)
        {
            try
            {
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this._audioWorkflowPool.Return(workflow);
                this.ActiveCallContext.CompleteUserAudioProcessing(turnId);
            }
            catch (ChannelClosedException)
            {
                this._audioWorkflowPool.Return(workflow);
                this.ActiveCallContext.CompleteUserAudioProcessing(turnId);
            }
            catch (Exception exception)
            {
                this._audioWorkflowPool.Return(workflow);
                this.ActiveCallContext.CompleteUserAudioProcessing(turnId);
                this.Logger.LogError(exception, "设备 {DeviceId} 的语音片段入队失败。", this.ActiveCallContext.DeviceId);
            }
        }

        public bool IsWaitingForReply => this.ActiveCallContext.DeviceContext.IsReplyInProgress;

        public void OnVoiceSilence()
        {
            this.ActiveCallContext.DeviceContext.AudioInPacket.TrimOldAudio();
        }

        public void OnLongTermSilence()
        {
            ActiveCallContext activeCall = this.ActiveCallContext;
            if (!activeCall.UserAgent.IsCallActive || this.IsWaitingForReply)
            {
                return;
            }
            this.AbortStreamingUtterance();
            this.Logger.LogDebug("设备 {deviceId} 检测到长时间静音，挂断电话。", activeCall.DeviceId);
            activeCall.MarkEnding();
            activeCall.UserAgent.Hangup();
        }

        protected override void OnHandlerTokenChanged()
        {
            this.AbortStreamingUtterance();
        }

        private void HandleStreamingAudio(float[] pcmData)
        {
            if (!this._streamingUtteranceActive || this._streamingUtteranceFailed)
            {
                return;
            }

            this.QueueStreamingOperation(pcmData, StreamingAsrOperation.Audio, this._streamingUtteranceTurnId);
        }

        private void StartStreamingUtterance()
        {
            if (this._asr is null || this._streamingUtteranceActive || this.HandlerToken.IsCancellationRequested)
            {
                return;
            }

            this._streamingUtteranceActive = true;
            this._streamingUtteranceFailed = false;
            this._streamingUtteranceTurnId = this.ActiveCallContext.TurnId;
            int preRollSamples = AudioProcessSettings.OutputToModelSampleRate
                * AudioProcessSettings.StreamingAsrPreRollMilliseconds / 1000;
            float[] preRollAudio = this.ActiveCallContext.DeviceContext.AudioInPacket.GetLatestAudio(preRollSamples);
            this.QueueStreamingOperation(preRollAudio, StreamingAsrOperation.Start, this._streamingUtteranceTurnId);
        }

        private bool FinishStreamingUtterance()
        {
            if (this._asr is null || !this._streamingUtteranceActive)
            {
                return false;
            }

            this._streamingUtteranceActive = false;
            this.QueueStreamingOperation([], StreamingAsrOperation.Finish, this._streamingUtteranceTurnId);
            return true;
        }

        private void AbortStreamingUtterance()
        {
            if (this._asr?.IsStreaming != true)
            {
                return;
            }

            this._streamingUtteranceActive = false;
            long turnId = this._streamingUtteranceTurnId;
            this._streamingUtteranceTurnId = -1;
            if (turnId < 0)
            {
                return;
            }

            this.ObserveStreamingOperation(
                this.SendStreamingOperationAsync([], StreamingAsrOperation.Abort, turnId, CancellationToken.None),
                StreamingAsrOperation.Abort,
                turnId);
        }

        private void QueueStreamingOperation(float[] audioData, StreamingAsrOperation operation, long turnId)
        {
            if (this._asr is null)
            {
                return;
            }

            if (operation == StreamingAsrOperation.Audio &&
                Interlocked.Increment(ref this._queuedStreamingAudioFrames) > this._maxQueuedStreamingAudioFrames)
            {
                Interlocked.Decrement(ref this._queuedStreamingAudioFrames);
                this._streamingUtteranceFailed = true;
                this.Logger.LogWarning("设备 {DeviceId} 的流式 ASR 音频队列已满，已中止当前语音。", this.ActiveCallContext.DeviceId);
                this.AbortStreamingUtterance();
                return;
            }

            lock (this._streamingQueueGate)
            {
                CancellationToken operationToken = this.HandlerToken;
                this._streamingOperationTail = this._streamingOperationTail
                    .ContinueWith(
                        _ => this.SendStreamingOperationAsync(audioData, operation, turnId, operationToken),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default)
                    .Unwrap();
            }

            this.ObserveStreamingOperation(this._streamingOperationTail, operation, turnId);
        }

        private async Task SendStreamingOperationAsync(
            float[] audioData,
            StreamingAsrOperation operation,
            long turnId,
            CancellationToken token)
        {
            IAsr? asr = this._asr;
            if (asr is null)
            {
                return;
            }

            Workflow<float[]> workflow = this._audioWorkflowPool.Get();
            try
            {
                workflow.Initialize(this.ActiveCallContext, audioData, turnId);
                await asr.ConvertSpeechTextStreamingAsync(
                    workflow,
                    AudioProcessSettings.OutputToModelSampleRate,
                    operation,
                    token);
            }
            finally
            {
                this._audioWorkflowPool.Return(workflow);
            }
        }

        private void ObserveStreamingOperation(Task operationTask, StreamingAsrOperation operation, long turnId)
        {
            _ = operationTask.ContinueWith(
                completed =>
                {
                    if (operation == StreamingAsrOperation.Audio)
                    {
                        Interlocked.Decrement(ref this._queuedStreamingAudioFrames);
                    }

                    if (completed.IsCanceled)
                    {
                        return;
                    }

                    if (completed.Exception is null)
                    {
                        return;
                    }

                    if (operation is StreamingAsrOperation.Start or StreamingAsrOperation.Finish)
                    {
                        this._streamingUtteranceActive = false;
                        this._streamingUtteranceFailed = true;
                        this.ActiveCallContext.CompleteUserAudioProcessing(turnId);
                    }

                    this.Logger.LogError(
                        completed.Exception,
                        "设备 {DeviceId} 的流式 ASR {Operation} 操作失败。",
                        this.ActiveCallContext.DeviceId,
                        operation);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        protected override void DisposeResources()
        {
        }

    }
}
