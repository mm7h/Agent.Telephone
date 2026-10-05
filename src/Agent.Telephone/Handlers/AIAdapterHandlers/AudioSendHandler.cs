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
        private IAudioProcessor? _audioProcessor;

        public AudioSendHandler(
            ObjectPool<Workflow<MixedAudioPacket>> mixedAudioWorkflowPool,
            ObjectPool<MixedAudioPacket> mixedAudioPacketPool,
            TelephoneConfig config,
            ILogger<AudioSendHandler> logger)
            : base(config, logger)
        {
            this._mixedAudioWorkflowPool = mixedAudioWorkflowPool;
            this._mixedAudioPacketPool = mixedAudioPacketPool;
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
                await this.DiscardFinalPlaybackAsync(workflow);
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
                await this.DiscardFinalPlaybackAsync(workflow);
                this.Logger.LogDebug("设备 {deviceId} 的混音音频发送已取消。", this.ActiveCallContext.DeviceId);
            }
            catch (Exception exception)
            {
                await this.DiscardFinalPlaybackAsync(workflow);
                this.Logger.LogError(exception, "设备 {deviceId} 的混音音频发送失败。", this.ActiveCallContext.DeviceId);
            }
        }

        private async Task DiscardFinalPlaybackAsync(Workflow<MixedAudioPacket> workflow)
        {
            if (!workflow.Data.IsLastFrame)
            {
                return;
            }

            this.ActiveCallContext.CompletePromptPlayback(fullyPlayed: false);
            this.ActiveCallContext.CompleteHangupAfterReply(workflow.TurnId);
        }

        private async Task MarkFinalPlaybackAsync(
            Workflow<MixedAudioPacket> workflow,
            bool fullyPlayed)
        {
            if (!workflow.Data.IsLastFrame)
            {
                return;
            }

            bool hangupAfterReply = this.ActiveCallContext.IsHangupAfterReplyPending(workflow.TurnId);
            if (fullyPlayed && (this.ActiveCallContext.IsPromptPlaybackPending || hangupAfterReply))
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(this.ActiveCallContext.PacketTimeMs),
                        this.HandlerToken);
                }
                catch (OperationCanceledException)
                {
                    fullyPlayed = false;
                }
            }

            this.ActiveCallContext.CompletePromptPlayback(fullyPlayed);
            if (hangupAfterReply)
            {
                this.ActiveCallContext.CompleteHangupAfterReply(workflow.TurnId);
            }
        }

        protected override void OnHandlerTokenChanged()
        {
            if (this.ActiveCallContext.CallToken.IsCancellationRequested ||
                this.HandlerToken.IsCancellationRequested)
            {
                return;
            }

            long interruptedTurnId = this.ActiveCallContext.TurnId - 1;
            if (interruptedTurnId < 0)
            {
                return;
            }

            _ = this.DiscardInterruptedTurnAsync(interruptedTurnId);
        }

        private async Task DiscardInterruptedTurnAsync(long turnId)
        {
            try
            {
                await Task.CompletedTask;
            }
            catch (Exception exception)
            {
                this.Logger.LogWarning(
                    exception,
                    "丢弃被打断的通话 {CallId} 轮次 {TurnId} 时失败。",
                    this.ActiveCallContext.CallId,
                    turnId);
            }
        }

        protected override void DisposeResources()
        {
            this.ActiveCallContext?.CompletePromptPlayback(fullyPlayed: false);
        }
    }
}
