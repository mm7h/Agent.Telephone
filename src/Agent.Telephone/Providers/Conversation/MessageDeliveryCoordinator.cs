using System.Collections.Concurrent;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Providers.CallControl.Reservations;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers.AIAdapterHandlers;
using Agent.Telephone.Management;
using Agent.Telephone.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers.Conversation
{
    internal interface IMessageDeliveryCoordinator
    {
        Task<bool> TryDeliverAsync(MessageRecord message, CancellationToken cancellationToken);
    }

    internal sealed class DeferredMessageDeliveryCoordinator : IMessageDeliveryCoordinator
    {
        private readonly DeviceContextManager _deviceManager;
        private readonly SIPTransport _sipTransport;
        private readonly TelephoneConfig _config;
        private readonly IMessageStore _messageStore;
        private readonly IServiceProvider _serviceProvider;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ILogger<DeferredMessageDeliveryCoordinator> _logger;

        public DeferredMessageDeliveryCoordinator(
            DeviceContextManager deviceManager,
            SIPTransport sipTransport,
            TelephoneConfig config,
            IMessageStore messageStore,
            IServiceProvider serviceProvider,
            IHostApplicationLifetime applicationLifetime,
            ILogger<DeferredMessageDeliveryCoordinator> logger)
        {
            this._deviceManager = deviceManager;
            this._sipTransport = sipTransport;
            this._config = config;
            this._messageStore = messageStore;
            this._serviceProvider = serviceProvider;
            this._applicationLifetime = applicationLifetime;
            this._logger = logger;
        }

        public async Task<bool> TryDeliverAsync(
            MessageRecord message,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource deliveryCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    this._applicationLifetime.ApplicationStopping);
            cancellationToken = deliveryCts.Token;

            MessageRecord? stored = await this._messageStore
                .GetAsync(
                    message.UserAor,
                    message.AssistantNumber,
                    message.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored?.WavePath is null)
            {
                this._logger.LogWarning("留言 {MessageId} 没有可播放的 WAV 文件。", message.Id);
                return false;
            }

            RegisteredEndpointResolution resolution = await this._deviceManager
                .AcquireCallbackAsync(message.UserAor, cancellationToken)
                .ConfigureAwait(false);
            if (resolution.Status != RegisteredEndpointStatus.Available ||
                resolution.Lease is null)
            {
                this._logger.LogInformation(
                    "留言 {MessageId} 暂不回拨，注册设备状态为 {Status}。",
                    message.Id,
                    resolution.Status);
                return false;
            }

            using IRegisteredEndpointLease endpointLease = resolution.Lease;
            await this.SaveStateAsync(stored, DeliveryState.Dialing, cancellationToken)
                .ConfigureAwait(false);

            AudioFormat negotiatedFormat = AudioFormat.Empty;
            AudioEncoder encoder = new(SupportedAudioFormats.SupportedSDPAudioFormat);
            AudioExtrasSource source = new(
                encoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
            source.RestrictFormats(format =>
                SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));
            VoIPMediaSession mediaSession = new(
                new MediaEndPoints { AudioSource = source })
            {
                AcceptRtpFromAny = true
            };
            mediaSession.OnAudioFormatsNegotiated += formats =>
            {
                negotiatedFormat = formats.FirstOrDefault(format =>
                    SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));
            };

            SIPUserAgent userAgent = new(this._sipTransport, SIPEndPoint.Empty, false);
            using CancellationTokenSource callEnded = new();
            DeviceContext? callbackDevice = null;
            ActiveCallContext? callbackCall = null;
            IDisposable? callbackCallLease = null;
            IAudioProcessor? audioProcessor = null;
            bool delivered = false;
            userAgent.OnCallHungup += OnCallHungup;

            void OnCallHungup(SIPDialogue dialogue)
            {
                callEnded.Cancel();
                callbackCall?.Cancel();
            }

            try
            {
                SIPURI origin = SIPURI.ParseSIPURI(message.UserAor);
                SIPCallDescriptor descriptor = new(
                    endpointLease.Endpoint.ContactUri,
                    string.Empty);
                descriptor.SetGeneralFromHeaderFields(
                    null,
                    message.AssistantNumber,
                    origin.Host);

                using CancellationTokenRegistration cancellationRegistration =
                    cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (userAgent.IsCallActive)
                            {
                                userAgent.Hangup();
                            }
                            else
                            {
                                userAgent.Cancel();
                            }
                        }
                        catch (Exception exception)
                        {
                            this._logger.LogDebug(exception, "取消留言回拨时发生异常。");
                        }
                    });

                bool answered = await userAgent.Call(
                    descriptor,
                    mediaSession,
                    ringTimeout: Math.Max(1, this._config.SIPConfig.CallbackTimeoutSeconds));
                if (!answered || negotiatedFormat.IsEmpty())
                {
                    return false;
                }

                if (!this._deviceManager.TryAttachCallbackCallSession(
                        message.UserAor,
                        message.AssistantNumber,
                        userAgent,
                        mediaSession,
                        out callbackDevice,
                        out callbackCall) ||
                    callbackDevice is null ||
                    callbackCall is null)
                {
                    return false;
                }

                callbackCall.NegotiatedAudioFormat = negotiatedFormat;
                callbackCall.PauseAgentMedia();
                if (!callbackCall.TryAcquireUse(out callbackCallLease) ||
                    callbackCallLease is null)
                {
                    return false;
                }

                await this.SaveStateAsync(stored, DeliveryState.Delivering, cancellationToken)
                    .ConfigureAwait(false);

                using CancellationTokenSource playbackCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        callEnded.Token);
                audioProcessor = this._serviceProvider.GetRequiredService<IAudioProcessor>();
                if (!audioProcessor.Build(ModelSetting.Empty))
                {
                    this._logger.LogError("Unable to build audio processor for callback message {MessageId}.", message.Id);
                    return false;
                }

                bool played = await audioProcessor.PlayFileAsync(
                    stored.WavePath,
                    mediaSession,
                    negotiatedFormat,
                    playbackCts.Token).ConfigureAwait(false);
                if (!played)
                {
                    return false;
                }

                await this._messageStore.MarkReadAsync(
                    stored.UserAor,
                    stored.AssistantNumber,
                    stored.Id,
                    cancellationToken).ConfigureAwait(false);
                delivered = true;

                if (callEnded.IsCancellationRequested ||
                    cancellationToken.IsCancellationRequested)
                {
                    return true;
                }

                using CancellationTokenSource pipelineCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        callEnded.Token,
                        callbackCall.CallToken);
                bool pipelineReady;
                try
                {
                    pipelineReady = await this.BuildCallbackPipelineAsync(
                        callbackDevice,
                        pipelineCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (pipelineCts.IsCancellationRequested)
                {
                    return true;
                }
                if (!pipelineReady)
                {
                    this._logger.LogWarning(
                        "留言 {MessageId} 已完整投递，但回拨 Agent 管线初始化失败。",
                        message.Id);
                    return true;
                }

                pipelineCts.Token.ThrowIfCancellationRequested();
                callbackCall.ResumeAgentMedia();
                using CancellationTokenSource conversationCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        callEnded.Token);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, conversationCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (conversationCts.IsCancellationRequested)
                {
                }
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return delivered;
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "回拨投递留言 {MessageId} 失败。", message.Id);
                return delivered;
            }
            finally
            {
                callbackCall?.ResumeAgentMedia();
                userAgent.OnCallHungup -= OnCallHungup;
                try
                {
                    if (userAgent.IsCallActive)
                    {
                        userAgent.Hangup();
                    }
                    else
                    {
                        userAgent.Cancel();
                    }
                }
                catch (Exception exception)
                {
                    this._logger.LogDebug(exception, "清理留言回拨呼叫时发生异常。");
                }
                if (callbackDevice is not null && callbackCall is not null)
                {
                    callbackDevice.CloseCallSession(callbackCall);
                }
                callbackCallLease?.Dispose();
                audioProcessor?.Dispose();
                mediaSession.Close("message delivery ended");
            }
        }

        private Task SaveStateAsync(
            MessageRecord message,
            DeliveryState state,
            CancellationToken cancellationToken)
        {
            return this._messageStore.SaveAsync(
                message with
                {
                    State = state,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken: cancellationToken);
        }

        private async Task<bool> BuildCallbackPipelineAsync(
            DeviceContext deviceContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FunctionToolManager functionToolManager =
                this._serviceProvider.GetRequiredService<FunctionToolManager>();
            ProviderManager providerManager =
                this._serviceProvider.GetRequiredService<ProviderManager>();
            HandlerManager handlerManager =
                this._serviceProvider.GetRequiredService<HandlerManager>();

            if (!await functionToolManager
                    .BuildForActiveCallAsync(deviceContext, this._sipTransport)
                    .ConfigureAwait(false))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await providerManager
                    .BuildForActiveCallAsync(deviceContext)
                    .ConfigureAwait(false))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await handlerManager
                .BuildForConnectedCallAsync(deviceContext)
                .ConfigureAwait(false);
        }
    }

    internal sealed class InboundMessagePlayer : IDisposable
    {
        private readonly IMessageStore _messageStore;
        private readonly ILogger<InboundMessagePlayer> _logger;
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentDictionary<string, Task> _playbacks = new();

        public InboundMessagePlayer(
            IMessageStore messageStore,
            ILogger<InboundMessagePlayer> logger)
        {
            this._messageStore = messageStore;
            this._logger = logger;
        }

        public void Start(
            ActiveCallContext activeCall,
            Text2AudioHandler promptSynthesizer)
        {
            if (!activeCall.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                return;
            }

            Task playback = this.PlayUnreadAsync(activeCall, promptSynthesizer, lease);
            if (!this._playbacks.TryAdd(activeCall.CallId, playback))
            {
                lease.Dispose();
                return;
            }

            _ = playback.ContinueWith(
                completedTask =>
                {
                    this._playbacks.TryRemove(activeCall.CallId, out Task? removed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task PlayUnreadAsync(
            ActiveCallContext activeCall,
            Text2AudioHandler promptSynthesizer,
            IDisposable lease)
        {
            using (lease)
            using (CancellationTokenSource playbackCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    activeCall.CallToken,
                    this._stopping.Token))
            {
                activeCall.PauseAgentMedia();
                try
                {
                    string assistantNumber = activeCall.DialedNumber
                        ?? throw new InvalidOperationException("The call has no assistant number.");
                    IReadOnlyList<MessageRecord> unread = await this._messageStore
                        .GetUnreadAsync(
                            activeCall.UserAor,
                            assistantNumber,
                            playbackCts.Token)
                        .ConfigureAwait(false);

                    if (unread.Count > 0)
                    {
                        this._logger.LogInformation(
                            "用户 {UserAor} 在 Assistant {AssistantNumber} 下有 {Count} 条未读留言。",
                            activeCall.UserAor,
                            assistantNumber,
                            unread.Count);

                        float[] countPrompt = await promptSynthesizer
                            .SynthesizePromptAsync(
                                $"您有 {unread.Count} 个电话留言。",
                                playbackCts.Token)
                            .ConfigureAwait(false);
                        await this.PlayPcmAsync(
                            activeCall,
                            countPrompt,
                            playbackCts.Token).ConfigureAwait(false);
                    }

                    foreach (MessageRecord message in unread)
                    {
                        IAudioProcessor? audioProcessor = activeCall.AIAgentContext
                            .PrivateProvider
                            .AudioProcessor;
                        if (audioProcessor is null)
                        {
                            this._logger.LogWarning(
                                "No audio processor is available for call {CallId} message playback.",
                                activeCall.CallId);
                            break;
                        }

                        bool completed = await audioProcessor.PlayFileAsync(
                            message.WavePath,
                            activeCall.VoIPRTP,
                            activeCall.NegotiatedAudioFormat,
                            playbackCts.Token).ConfigureAwait(false);
                        if (!completed)
                        {
                            break;
                        }

                        await this._messageStore.MarkReadAsync(
                            message.UserAor,
                            message.AssistantNumber,
                            message.Id,
                            playbackCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (playbackCts.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    this._logger.LogError(
                        exception,
                        "呼入通话 {CallId} 播放未读留言失败。",
                        activeCall.CallId);
                }
                finally
                {
                    activeCall.ResumeAgentMedia();
                }
            }
        }

        private async Task PlayPcmAsync(
            ActiveCallContext activeCall,
            float[] audio,
            CancellationToken cancellationToken)
        {
            IAudioProcessor? audioProcessor =
                activeCall.AIAgentContext.PrivateProvider.AudioProcessor;
            AudioFormat audioFormat = activeCall.NegotiatedAudioFormat;
            int packetTimeMs = activeCall.PacketTimeMs;
            int samplesPerPacket =
                AudioProcessSettings.ModelToInputSampleRate * packetTimeMs / 1000;
            int durationRtpUnits = audioFormat.ClockRate * packetTimeMs / 1000;
            if (audioProcessor is null ||
                audio.Length == 0 ||
                audioFormat.IsEmpty() ||
                packetTimeMs <= 0 ||
                samplesPerPacket <= 0 ||
                durationRtpUnits <= 0)
            {
                return;
            }

            for (int offset = 0; offset < audio.Length; offset += samplesPerPacket)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(samplesPerPacket, audio.Length - offset);
                float[] packet = new float[samplesPerPacket];
                audio.AsSpan(offset, length).CopyTo(packet);
                byte[] encoded = await audioProcessor.EncodeAsync(
                    packet,
                    audioFormat,
                    cancellationToken).ConfigureAwait(false);
                activeCall.VoIPRTP.SendAudio((uint)durationRtpUnits, encoded);
                await Task.Delay(packetTimeMs, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            this._stopping.Cancel();
            this._stopping.Dispose();
        }
    }
}
