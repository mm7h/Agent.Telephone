using System.Threading.Channels;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class AudioProcessorHandler : BaseHandler,
        IInAIAdapterHandler<OutAudioSegment>,
        IOutAIAdapterHandler<MixedAudioPacket>
    {
        private readonly ObjectPool<OutAudioSegment> _audioSegmentPool;
        private readonly ObjectPool<Workflow<OutAudioSegment>> _audioWorkflowPool;
        private readonly ObjectPool<MixedAudioPacket> _mixedAudioPacketPool;
        private readonly ObjectPool<Workflow<MixedAudioPacket>> _mixedAudioWorkflowPool;
        private IAudioProcessor? _audioProcessor;

        public AudioProcessorHandler(
            ObjectPool<OutAudioSegment> audioSegmentPool,
            ObjectPool<Workflow<OutAudioSegment>> audioWorkflowPool,
            ObjectPool<MixedAudioPacket> mixedAudioPacketPool,
            ObjectPool<Workflow<MixedAudioPacket>> mixedAudioWorkflowPool,
            TelephoneConfig config,
            ILogger<AudioProcessorHandler> logger)
            : base(config, logger)
        {
            this._audioSegmentPool = audioSegmentPool;
            this._audioWorkflowPool = audioWorkflowPool;
            this._mixedAudioPacketPool = mixedAudioPacketPool;
            this._mixedAudioWorkflowPool = mixedAudioWorkflowPool;
        }

        public override string HandlerName => nameof(AudioProcessorHandler);

        public ChannelReader<Workflow<OutAudioSegment>> PreviousReader { get; set; } = null!;

        public ChannelWriter<Workflow<MixedAudioPacket>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            ActiveCallContext activeCall = this.ActiveCallContext;
            if (activeCall?.AIAgentContext.PrivateProvider.AudioProcessor is not IAudioProcessor audioProcessor)
            {
                this.Logger.LogError("设备 {deviceId} 未配置音频处理器。", this.ActiveCallContext.DeviceId);
                return false;
            }

            this._audioProcessor = audioProcessor;
            this._audioProcessor.OnMixedAudioDataAvailable += this.OnMixedAudioDataAvailableAsync;
            this.RegisterCancellationToken(this.ActiveCallContext);
            return true;
        }

        public async Task HandleAsync()
        {
            await foreach (Workflow<OutAudioSegment> workflow in this.PreviousReader.ReadAllAsync())
            {
                try
                {
                    this.Handle(workflow);
                }
                finally
                {
                    this._audioSegmentPool.Return(workflow.Data);
                    this._audioWorkflowPool.Return(workflow);
                }
            }
        }

        private void Handle(Workflow<OutAudioSegment> workflow)
        {
            if (!this.CheckWorkflowValid(workflow) || this._audioProcessor is null)
            {
                return;
            }

            try
            {
                this.HandlerToken.ThrowIfCancellationRequested();
                OutAudioSegment segment = workflow.Data;
                AudioFormat format = this.ActiveCallContext.NegotiatedAudioFormat;
                if (format.IsEmpty())
                {
                    this.Logger.LogWarning("设备 {deviceId} 尚未协商 RTP 音频格式，暂不处理混音音频。", this.ActiveCallContext.DeviceId);
                    return;
                }

                if (this.ActiveCallContext.PacketTimeMs <= 0)
                {
                    this.Logger.LogWarning("设备 {deviceId} 的 RTP ptime 无效，暂不处理混音音频。", this.ActiveCallContext.DeviceId);
                    return;
                }

                if (!this._audioProcessor.InitializeMixer(
                    format.ClockRate,
                    outputChannels: 1,
                    this.ActiveCallContext.PacketTimeMs))
                {
                    this.Logger.LogError(
                        "设备 {deviceId} 无法为 {codec}/{sampleRate}Hz、ptime={ptime}ms 初始化混音器。",
                        this.ActiveCallContext.DeviceId,
                        format.Codec,
                        format.ClockRate,
                        this.ActiveCallContext.PacketTimeMs);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(segment.SentenceId))
                {
                    this._audioProcessor.RegisterSubtitle(
                        segment.SentenceId,
                        segment.AudioType,
                        segment.IsFirstFrame ? TtsStatus.SentenceStart : TtsStatus.SentenceEnd,
                        segment.Content);
                }

                this._audioProcessor.ProcessAudio(
                    segment.AudioType,
                    segment.AudioData,
                    segment.SentenceId);

                if (segment.IsLastSegment)
                {
                    this._audioProcessor.CompleteStream(segment.AudioType);
                }
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("设备 {deviceId} 的混音输入已取消。", this.ActiveCallContext.DeviceId);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "设备 {deviceId} 的混音输入失败。", this.ActiveCallContext.DeviceId);
            }
        }

        protected override void OnHandlerTokenChanged()
        {
            this._audioProcessor?.ClearAllBuffers();
        }

        private async void OnMixedAudioDataAvailableAsync(float[] mixedPcmData, bool isFirst, bool isLast, string? sentenceId)
        {
            if (this.HandlerToken.IsCancellationRequested)
            {
                return;
            }

            MixedAudioPacket packet = this._mixedAudioPacketPool.Get();
            Workflow<MixedAudioPacket> workflow = this._mixedAudioWorkflowPool.Get();
            try
            {
                packet.Initialize(mixedPcmData, isFirst, isLast, sentenceId);
                workflow.Initialize(this.ActiveCallContext, packet);
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this._mixedAudioPacketPool.Return(packet);
                this._mixedAudioWorkflowPool.Return(workflow);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "设备 {deviceId} 的混音输出写入失败。", this.ActiveCallContext.DeviceId);
                this._mixedAudioPacketPool.Return(packet);
                this._mixedAudioWorkflowPool.Return(workflow);
            }
        }

        public override void Dispose()
        {
            if (this._audioProcessor is not null)
            {
                this._audioProcessor.OnMixedAudioDataAvailable -= this.OnMixedAudioDataAvailableAsync;
            }

            base.Dispose();
        }
    }
}
