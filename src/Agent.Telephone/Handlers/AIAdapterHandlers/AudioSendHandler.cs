using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal sealed class AudioSendHandler : BaseHandler, IInAIAdapterHandler<MixedAudioPacket>
    {
        private readonly ObjectPool<Workflow<MixedAudioPacket>> _mixedAudioWorkflowPool;
        private readonly ObjectPool<MixedAudioPacket> _mixedAudioPacketPool;
        private readonly IOfflineDialogue _offlineDialogue;
        private IAudioProcessor? _audioProcessor;

        public AudioSendHandler(
            ObjectPool<Workflow<MixedAudioPacket>> mixedAudioWorkflowPool,
            ObjectPool<MixedAudioPacket> mixedAudioPacketPool,
            IOfflineDialogue offlineDialogue,
            TelephoneConfig config,
            ILogger<AudioSendHandler> logger)
            : base(config, logger)
        {
            this._mixedAudioWorkflowPool = mixedAudioWorkflowPool;
            this._mixedAudioPacketPool = mixedAudioPacketPool;
            this._offlineDialogue = offlineDialogue;
        }

        public override string HandlerName => HandlerNames.AudioSendHandlerName;

        public ChannelReader<Workflow<MixedAudioPacket>> PreviousReader { get; set; } = null!;

        public override bool Build()
        {
            ActiveCallContext activeCall = this.ActiveCallContext;
            if (activeCall?.AIAgentContext.PrivateProvider.AudioProcessor is not IAudioProcessor audioProcessor)
            {
                this.Logger.LogError("设备 {deviceId} 未配置音频处理器。", this.ActiveCallContext.DeviceId);
                return false;
            }

            this._audioProcessor = audioProcessor;
            this.RegisterCancellationToken(this.ActiveCallContext);
            return true;
        }

        public async Task HandleAsync()
        {
            await foreach (Workflow<MixedAudioPacket> workflow in this.PreviousReader.ReadAllAsync())
            {
                try
                {
                    await this.HandleAsync(workflow);
                }
                finally
                {
                    this._mixedAudioPacketPool.Return(workflow.Data);
                    this._mixedAudioWorkflowPool.Return(workflow);
                }
            }
        }

        private async Task HandleAsync(Workflow<MixedAudioPacket> workflow)
        {
            if (!this.CheckWorkflowValid(workflow) || this._audioProcessor is null)
            {
                return;
            }

            if (this.ActiveCallContext.IsAgentMediaPaused)
            {
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                return;
            }

            try
            {
                MixedAudioPacket packet = workflow.Data;
                if (!string.IsNullOrWhiteSpace(packet.SentenceId))
                {
                    this._audioProcessor.GetSubtitle(packet.SentenceId, out _);
                }

                if (packet.Data.Length > 0)
                {
                    byte[] encoded = await this._audioProcessor.EncodeAsync(
                        packet.Data,
                        this.ActiveCallContext.NegotiatedAudioFormat,
                        this.HandlerToken);
                    this.ActiveCallContext.VoIPRTP.SendAudio((uint)packet.Data.Length, encoded);
                }

                if (packet.IsLastFrame)
                {
                    await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: true);
                }
            }
            catch (OperationCanceledException)
            {
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                this.Logger.LogDebug("设备 {deviceId} 的混音音频发送已取消。", this.ActiveCallContext.DeviceId);
            }
            catch (Exception exception)
            {
                await this.MarkFinalPlaybackAsync(workflow, fullyPlayed: false);
                this.Logger.LogError(exception, "设备 {deviceId} 的混音音频发送失败。", this.ActiveCallContext.DeviceId);
            }
        }

        private Task MarkFinalPlaybackAsync(
            Workflow<MixedAudioPacket> workflow,
            bool fullyPlayed)
        {
            return workflow.Data.IsLastFrame
                ? this._offlineDialogue.MarkTurnPlaybackCompletedAsync(
                    this.ActiveCallContext,
                    workflow.TurnId,
                    fullyPlayed,
                    CancellationToken.None)
                : Task.CompletedTask;
        }
    }
}
