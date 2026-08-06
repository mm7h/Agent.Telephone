using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using SIPSorceryMedia.Abstractions;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class AudioSendHandler : BaseHandler, IInAIAdapterHandler<OutAudioSegment>
    {
        private IAudioProcessor? _audioProcessor;
        private readonly ObjectPool<Workflow<OutAudioSegment>> _audioWorkflowPool;
        private readonly ObjectPool<OutAudioSegment> _audioSegmentPool;
        private readonly IOfflineDialogue _offlineDialogue;

        public AudioSendHandler(
            ObjectPool<Workflow<OutAudioSegment>> audioWorkflowPool,
            ObjectPool<OutAudioSegment> audioSegmentPool,
            IOfflineDialogue offlineDialogue,
            TelephoneConfig config,
            ILogger<AudioSendHandler> logger) : base(config, logger)
        {
            this._audioWorkflowPool = audioWorkflowPool;
            this._audioSegmentPool = audioSegmentPool;
            this._offlineDialogue = offlineDialogue;
        }

        public override string HandlerName => HandlerNames.AudioSendHandlerName;
        public ChannelReader<Workflow<OutAudioSegment>> PreviousReader { get; set; } = null!;

        public override bool Build()
        {
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", this.DeviceContext.DeviceId);
                return false;
            }
            PrivateProvider privateProvider = this.DeviceContext.ActiveCall.AIAgentContext.PrivateProvider;
            if (privateProvider.AudioProcessor is null)
            {
                this.Logger.LogError("音频处理器未为设备配置: {deviceId}。", this.DeviceContext.DeviceId);
                return false;
            }

            this._audioProcessor = privateProvider.AudioProcessor;
            //this._audioProcessor.OnAudioDataAvailable

            this.RegisterCancellationToken(this.DeviceContext);
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
                    this._audioSegmentPool.Return(workflow.Data);
                    this._audioWorkflowPool.Return(workflow);
                }
            }
        }
        public async Task HandleAsync(Workflow<OutAudioSegment> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }

            if (this._audioProcessor is null)
            { 
                this.Logger.LogError("音频处理器未为设备配置: {deviceId}。", this.DeviceContext.DeviceId);
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                return;
            }

            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法发送音频。", this.DeviceContext.DeviceId);
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                return;
            }

            AudioFormat audioFormat = this.ActiveCallContext.NegotiatedAudioFormat;
            int packetTimeMs = this.ActiveCallContext.PacketTimeMs;
            int inputSamplesPerPacket = AudioProcessSettings.ModelToInputSampleRate * packetTimeMs / 1000;
            int durationRtpUnits = audioFormat.ClockRate * packetTimeMs / 1000;

            if (audioFormat.IsEmpty()
                || packetTimeMs <= 0
                || inputSamplesPerPacket <= 0
                || durationRtpUnits <= 0
                || AudioProcessSettings.ModelToInputSampleRate * packetTimeMs % 1000 != 0
                || audioFormat.ClockRate * packetTimeMs % 1000 != 0)
            {
                this.Logger.LogWarning("设备 {deviceId} 的 RTP 音频包参数无效。", this.DeviceContext.DeviceId);
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                return;
            }

            try
            {
                this.DeviceContext.AudioOutputPacket.PushAudio(workflow.Data.AudioData);

                float[] samples = this.DeviceContext.AudioOutputPacket.GetAllAudio();
                int analyzedIndex = 0;
                while (samples.GetSlidingFrame(inputSamplesPerPacket, ref analyzedIndex, out float[] frame))
                {
                    if (this.ActiveCallContext.IsAgentMediaPaused)
                    {
                        this.DeviceContext.AudioOutputPacket.Reset();
                        await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                        return;
                    }

                    byte[] encoded = await this._audioProcessor.EncodeAsync(frame, audioFormat, this.HandlerToken);
                    this.ActiveCallContext.VoIPRTP.SendAudio((uint)durationRtpUnits, encoded);
                    this.DeviceContext.AudioOutputPacket.PopFrames(inputSamplesPerPacket);

                    await Task.Delay(packetTimeMs, this.HandlerToken);
                }

                if (workflow.Data.IsLastSegment && workflow.Data.IsLastFrame)
                {
                    float[] remaining = this.DeviceContext.AudioOutputPacket.GetAllAudio();
                    if (remaining.Length > 0)
                    {
                        float[] padded = new float[inputSamplesPerPacket];
                        remaining.AsSpan().CopyTo(padded);
                        byte[] encoded = await this._audioProcessor.EncodeAsync(
                            padded,
                            audioFormat,
                            this.HandlerToken);
                        this.ActiveCallContext.VoIPRTP.SendAudio(
                            (uint)durationRtpUnits,
                            encoded);
                        this.DeviceContext.AudioOutputPacket.Reset();
                    }

                    await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: true);
                }
            }
            catch (OperationCanceledException)
            {
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                this.Logger.LogDebug("音频发送已取消，设备 {deviceId}。", this.DeviceContext.DeviceId);
            }
            catch (Exception exception)
            {
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                this.Logger.LogError(exception, "处理来自设备的音频发送数据包失败: {deviceId}。", this.DeviceContext.DeviceId);
            }
        }

        private Task MarkFinalPlaybackAsync(
            Workflow<OutAudioSegment> workflow,
            bool fullyPlayed)
        {
            return workflow.Data.IsLastSegment && workflow.Data.IsLastFrame
                ? this._offlineDialogue.MarkTurnPlaybackCompletedAsync(
                    this.ActiveCallContext,
                    workflow.TurnId,
                    fullyPlayed,
                    CancellationToken.None)
                : Task.CompletedTask;
        }

        protected override void OnHandlerTokenChanged()
        {
            this.DeviceContext.AudioOutputPacket.Reset();
        }
        public override void Dispose()
        {
            this.DeviceContext.AudioOutputPacket.Dispose();
            base.Dispose();
        }
    }
}
