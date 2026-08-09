using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.VAD;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class AudioReceivedHandler : BaseHandler, IInAIAdapterHandler<byte[]>, IOutAIAdapterHandler<float[]>, IVadEventCallback
    {

        private IAudioProcessor? _audioProcessor;
        private IVad? _vad;
        private readonly ObjectPool<Workflow<byte[]>> _rtpPacketWorkflowPool;
        private readonly ObjectPool<Workflow<float[]>> _audioWorkflowPool;

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
            this._vad = privateProvider.Vad;
            this._vad.RegisterDevice(this.ActiveCallContext.DeviceId, this);
            this._audioProcessor.RegisterDevice(this.ActiveCallContext.DeviceId);

            this.RegisterCancellationToken(this.ActiveCallContext);
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
                    this._rtpPacketWorkflowPool.Return(workflow);
                }
            }
        }
        public async Task HandleAsync(Workflow<byte[]> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
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

                await this._vad.AnalysisVoiceAsync(this.ActiveCallContext.DeviceId, this.ActiveCallContext.DeviceContext.AudioInPacket.GetAllAudio(), this.HandlerToken);
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
            this.ActiveCallContext.DeviceContext.AudioInPacket.ResetAudioBuffer();
            if (this.HandlerToken.IsCancellationRequested)
            {
                return;
            }
            if (audioData.Length < 50)
            {
                // Audio too short, cannot recognize
                this.Logger.LogDebug("设备 {deviceId} 的语音太短。", this.ActiveCallContext.DeviceId);
                
                // todo
                return;
            }

            var workflow = this._audioWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, audioData);

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

        public void OnVoiceSilence()
        {
            this.ActiveCallContext.DeviceContext.AudioInPacket.TrimOldAudio();
        }

        public void OnLongTermSilence()
        {
            // todo: 挂掉电话
        }

        public override void Dispose()
        {
            if (this._vad is not null)
            {
                this._vad.UnregisterDevice(this.ActiveCallContext.DeviceId);
            }

            this.NextWriter?.TryComplete();
            base.Dispose();
        }

    }
}
