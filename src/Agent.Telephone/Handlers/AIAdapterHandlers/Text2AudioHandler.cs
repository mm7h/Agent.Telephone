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

        public Text2AudioHandler(ObjectPool<Workflow<OutSegment>> segmentWorkflowPool,
            ObjectPool<OutSegment> segmentPool,
            ObjectPool<Workflow<float[]>> audioWorkflowPool,
            TelephoneConfig config,
            ILogger<Text2AudioHandler> logger) : base(config, logger)
        {
            this._segmentPool = segmentPool;
            this._segmentWorkflowPool = segmentWorkflowPool;
            this._audioWorkflowPool = audioWorkflowPool;
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
                if (string.IsNullOrWhiteSpace(workflow.Data.Content))
                {
                    this.Logger.LogInformation("无需TTS，查询文本为空。");
                    return;
                }
                this.HandlerToken.ThrowIfCancellationRequested();
                await this._tts.SynthesisAsync(workflow, this.HandlerToken);
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
            Workflow<float[]> workflow = this._audioWorkflowPool.Get();
            workflow.Initialize(this.DeviceContext, audioData);
            try
            {
                this.NextWriter.WriteAsync(workflow, this.HandlerToken);
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
                if (!this._tts.IsSherpaModel)
                {
                    this._tts.Dispose();
                }
            }
            this.NextWriter?.TryComplete();
            base.Dispose();
        }
    }
}
