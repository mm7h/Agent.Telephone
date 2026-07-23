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
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", this.DeviceContext.DeviceId);
                return false;
            }
            PrivateProvider privateProvider = this.DeviceContext.ActiveCall.AIAgentContext.PrivateProvider;
            if (privateProvider.Asr is null)
            {
                this.Logger.LogError("设备 {deviceId} 未配置 ASR 提供程序。", this.DeviceContext.DeviceId);
                return false;
            }
            this._asr = privateProvider.Asr;
            this._asr.RegisterDevice(this.DeviceContext.DeviceId, this);

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

        private async Task HandleAsync(Workflow<float[]> workflow)
        {
            if (!this.CheckWorkflowValid(workflow))
            {
                return;
            }
            if (this._asr is null)
            {
                this.Logger.LogError("ASR提供程序未为设备配置: {deviceId}。", this.DeviceContext.DeviceId);
                return;
            }

            try
            {
                await this._asr.ConvertSpeechTextAsync(workflow, AudioProcessSettings.ModelToInputSampleRate, this.HandlerToken);
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("ASR 处理已取消，设备 {DeviceId}", this.DeviceContext.DeviceId);
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "处理来自设备 {deviceId} 的语音转文本数据包失败。", this.DeviceContext.DeviceId);
            }
        }

        public void OnSpeechTextConverted(bool success, string text)
        {
            if (!success)
            {
                this.Logger.LogError("ASR 转换语音文本失败。");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                // todo
                //this.DeviceContext.Reset();
                this.Logger.LogDebug("设备 {deviceId} 未检测到语音。", this.DeviceContext.DeviceId);
                return;
            }
            if (this.DeviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法转换语音到文本。", this.DeviceContext.DeviceId);
                return;
            }
            this.Logger.LogDebug("设备 {deviceId} 检测到语音文本: {text}", this.DeviceContext.DeviceId, text);
            Workflow<string> workflow = this._textWorkflowPool.Get();
            workflow.Initialize(this.DeviceContext, text);
            try
            {
                this.NextWriter.WriteAsync(workflow, this.HandlerToken);
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
            if (this._asr is not null)
            {
                this._asr.UnregisterDevice(this.DeviceContext.DeviceId);
                if (!this._asr.IsSherpaModel)
                {
                    this._asr.Dispose();
                }
            }
            this.NextWriter?.TryComplete();
            base.Dispose();
        }
    }
}
