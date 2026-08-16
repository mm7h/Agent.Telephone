using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.ASR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class Audio2TextHandler : BaseHandler, IInAIAdapterHandler<float[]>, IOutAIAdapterHandler<string>, IAsrEventCallback
    {
        private IAsr? _asr;
        private readonly ObjectPool<Workflow<float[]>> _audioWorkflowPool;
        private readonly ObjectPool<Workflow<string>> _textWorkflowPool;

        public Audio2TextHandler(ObjectPool<Workflow<float[]>> audioWorkflowPool, ObjectPool<Workflow<string>> textWorkflowPool,
            TelephoneConfig config, 
            ILogger<Audio2TextHandler> logger) : base(config, logger)
        {
            this._audioWorkflowPool = audioWorkflowPool;
            this._textWorkflowPool = textWorkflowPool;
        }

        public override string HandlerName => HandlerNames.Audio2TextHandlerName;
        public ChannelReader<Workflow<float[]>> PreviousReader { get; set; } = null!;
        public ChannelWriter<Workflow<string>> NextWriter { get; set; } = null!;

        public override bool Build()
        {
            PrivateProvider privateProvider = this.ActiveCallContext.AIAgentContext.PrivateProvider;
            if (privateProvider.Asr is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 ASR 提供程序。", this.ActiveCallContext.DeviceId);
                return false;
            }
            this._asr = privateProvider.Asr;
            this._asr.RegisterDevice(this.ActiveCallContext, this);

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
                    this._audioWorkflowPool.Return(workflow);
                }
            }
        }

        private async Task HandleAsync(Workflow<float[]> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }
            if (this._asr is null)
            {
                this.Logger.LogError("ASR提供程序未为设备配置: {deviceId}。", this.ActiveCallContext.DeviceId);
                return;
            }

            try
            {
                await this._asr.ConvertSpeechTextAsync(workflow, AudioProcessSettings.OutputToModelSampleRate, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("ASR 处理已取消，设备 {DeviceId}", this.ActiveCallContext.DeviceId);
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "处理来自设备 {deviceId} 的语音转文本数据包失败。", this.ActiveCallContext.DeviceId);
            }
        }

        public async void OnSpeechTextConverted(bool success, string text)
        {
            if (!success)
            {
                this.Logger.LogError("ASR 转换语音文本失败。");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                // todo
                this.Logger.LogDebug("设备 {deviceId} 未检测到语音。", this.ActiveCallContext.DeviceId);
                return;
            }
            this.Logger.LogDebug("设备 {deviceId} 检测到语音文本: {text}", this.ActiveCallContext.DeviceId, text);
            this.ActiveCallContext.RestartTurn();
            Workflow<string> workflow = this._textWorkflowPool.Get();
            workflow.Initialize(this.ActiveCallContext, text);
            try
            {
                await this.NextWriter.WriteAsync(workflow, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this._textWorkflowPool.Return(workflow);
            }
            catch
            {
                this._textWorkflowPool.Return(workflow);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
        }
    }
}
