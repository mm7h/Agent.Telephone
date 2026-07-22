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
    internal sealed class AudioSendHandler : BaseHandler, IInAIAdapterHandler<float[]>
    {
        private IAudioProcessor? _audioProcessor;
        private readonly ObjectPool<Workflow<float[]>> _audioWorkflowPool;

        public AudioSendHandler(
            ObjectPool<Workflow<float[]>> audioWorkflowPool,
            TelephoneConfig config,
            ILogger<AudioSendHandler> logger) : base(config, logger)
        {
            this._audioWorkflowPool = audioWorkflowPool;
        }

        public override string HandlerName => HandlerNames.AudioSendHandlerName;
        public ChannelReader<Workflow<float[]>> PreviousReader { get; set; } = null!;

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
                    this._audioWorkflowPool.Return(workflow);
                }
            }
        }
        public async Task HandleAsync(Workflow<float[]> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }

            if (this._audioProcessor is null)
            { 
                this.Logger.LogError("音频处理器未为设备配置: {deviceId}。", this.DeviceContext.DeviceId);
                return;
            }

            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法发送音频。", this.DeviceContext.DeviceId);
                return;
            }

            AudioFormat audioFormat = this.DeviceContext.ActiveCall.NegotiatedAudioFormat;
            int packetTimeMs = this.DeviceContext.ActiveCall.PacketTimeMs;
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
                return;
            }

            try
            {
                this.DeviceContext.AudioOutputPacket.PushAudio(workflow.Data);

                float[] samples = this.DeviceContext.AudioOutputPacket.GetAllAudio();
                int analyzedIndex = 0;
                while (samples.GetSlidingFrame(inputSamplesPerPacket, ref analyzedIndex, out float[] frame))
                {
                    byte[] encoded = await this._audioProcessor.EncodeAsync(frame, audioFormat, this.HandlerToken);
                    this.DeviceContext.ActiveCall.VoIPRTP.SendAudio((uint)durationRtpUnits, encoded);
                    this.DeviceContext.AudioOutputPacket.PopFrames(inputSamplesPerPacket);

                    await Task.Delay(packetTimeMs, this.HandlerToken);
                }
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("音频发送已取消，设备 {deviceId}。", this.DeviceContext.DeviceId);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "处理来自设备的音频发送数据包失败: {deviceId}。", this.DeviceContext.DeviceId);
            }
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
