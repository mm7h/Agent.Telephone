using System.Collections.Concurrent;
using System.Threading.Channels;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Management;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers.OfflineDialogue
{
    internal sealed class DefaultOfflineDialogue : BaseProvider<DefaultOfflineDialogue, ModelSetting>, IOfflineDialogue, IAsyncDisposable
    {
        private const int DtmfTimeoutSeconds = 15;
        private const int PersistenceQueueCapacity = 128;
        private static readonly TimeSpan s_generatingMessagePollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan s_messagePlaybackInterval = TimeSpan.FromSeconds(1.5);
        private readonly ITelephoneStore _telephoneStore;
        private readonly SIPTransport _sipTransport;
        private readonly FunctionToolManager _functionToolManager;
        private readonly ProviderManager _providerManager;
        private readonly HandlerManager _handlerManager;
        private readonly TelephoneConfig _config;
        private readonly Channel<PersistenceOperation> _operations = Channel.CreateBounded<PersistenceOperation>(
            new BoundedChannelOptions(PersistenceQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly ConcurrentDictionary<string, Task> _playbacks = new();
        private Task? _writerTask;
        private int _disposed;

        public DefaultOfflineDialogue(
            ITelephoneStore telephoneStore,
            SIPTransport sipTransport,
            FunctionToolManager functionToolManager,
            ProviderManager providerManager,
            HandlerManager handlerManager,
            TelephoneConfig config,
            ILogger<DefaultOfflineDialogue> logger)
            : base(logger)
        {
            this._telephoneStore = telephoneStore;
            this._sipTransport = sipTransport;
            this._functionToolManager = functionToolManager;
            this._providerManager = providerManager;
            this._handlerManager = handlerManager;
            this._config = config;
        }

        public override string ProviderType => "offline-dialogue";
        public override string ModelName => nameof(DefaultOfflineDialogue);

        public override bool Build(ModelSetting settings)
        {
            this._writerTask ??= this.ProcessQueueAsync();
            return true;
        }

        public ValueTask<OfflineDialogueTurn> BeginTurnAsync(
            ActiveCallContext activeCall,
            long turnId,
            string userText,
            CancellationToken cancellationToken)
        {
            string assistantNumber = activeCall.DialedNumber
                ?? throw new InvalidOperationException("The call has no assistant number.");
            string turnKey = $"{activeCall.CallId}:{turnId}";
            string userMessageId = Guid.NewGuid().ToString("N");
            var turn = new OfflineDialogueTurn(
                turnKey,
                activeCall.UserAor,
                assistantNumber,
                userMessageId,
                Guid.NewGuid().ToString("N"),
                userText);
            return ValueTask.FromResult(turn);
        }

        public async ValueTask BeginOfflinePersistenceAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken)
        {
            if (!turn.TryBeginPersistence(out IReadOnlyList<OfflineDialogueSegment> segments))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await this.EnqueueAsync(turn, async token =>
            {
                await this._telephoneStore.SaveConversationMessageAsync(new ConversationMessage
                {
                    Id = turn.UserMessageId,
                    TurnId = turn.TurnId,
                    UserAor = turn.UserAor,
                    AssistantNumber = turn.AssistantNumber,
                    Role = ConversationRole.User,
                    FullText = turn.UserText,
                    State = DeliveryState.Read,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ReadAt = now,
                }, token);
                await this._telephoneStore.SaveMessageSegmentAsync(new MessageSegment
                {
                    MessageId = turn.UserMessageId,
                    Sequence = 1,
                    TextContent = turn.UserText,
                    CreatedAt = now,
                }, token);
                await this._telephoneStore.SaveConversationMessageAsync(new ConversationMessage
                {
                    Id = turn.AssistantMessageId,
                    TurnId = turn.TurnId,
                    UserAor = turn.UserAor,
                    AssistantNumber = turn.AssistantNumber,
                    Role = ConversationRole.Assistant,
                    State = DeliveryState.Generating,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, token);

                foreach (OfflineDialogueSegment segment in segments)
                {
                    await this._telephoneStore.SaveMessageSegmentAsync(new MessageSegment
                    {
                        MessageId = turn.AssistantMessageId,
                        Sequence = segment.Sequence,
                        TextContent = segment.TextContent,
                        AudioPath = segment.AudioPath,
                    }, token);
                }
            }, isTerminal: false, cancellationToken);
        }

        public async ValueTask AppendAssistantSegmentAsync(
            OfflineDialogueTurn turn,
            string textContent,
            string? audioPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(textContent))
            {
                return;
            }

            OfflineDialogueSegment segment = turn.AddAssistantSegment(textContent, audioPath);
            if (!turn.IsPersistenceStarted)
            {
                return;
            }

            await this.EnqueueAsync(turn, token => this._telephoneStore.SaveMessageSegmentAsync(new MessageSegment
            {
                MessageId = turn.AssistantMessageId,
                Sequence = segment.Sequence,
                TextContent = segment.TextContent,
                AudioPath = segment.AudioPath,
            }, token), isTerminal: false, cancellationToken);
        }

        public ValueTask CompleteTurnAsync(OfflineDialogueTurn turn, bool read, CancellationToken cancellationToken)
        {
            if (!turn.IsPersistenceStarted)
            {
                turn.Completion.TrySetResult(true);
                return ValueTask.CompletedTask;
            }

            return this.EnqueueAsync(turn, token => this._telephoneStore.FinalizeAssistantMessageAsync(
                turn.UserAor,
                turn.AssistantNumber,
                turn.AssistantMessageId,
                turn.GetFullText(),
                read ? DeliveryState.Read : DeliveryState.Unread,
                token), isTerminal: true, cancellationToken);
        }

        public ValueTask FailTurnAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken)
        {
            if (!turn.IsPersistenceStarted)
            {
                turn.Completion.TrySetResult(true);
                return ValueTask.CompletedTask;
            }

            return this.EnqueueAsync(turn, token => this._telephoneStore.FailAssistantMessageAsync(
                turn.UserAor,
                turn.AssistantNumber,
                turn.AssistantMessageId,
                turn.GetFullText(),
                token), isTerminal: true, cancellationToken);
        }

        public ValueTask DiscardTurnAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken)
        {
            if (!turn.IsPersistenceStarted)
            {
                turn.Completion.TrySetResult(true);
                return ValueTask.CompletedTask;
            }

            return this.EnqueueAsync(turn, token => this._telephoneStore.DiscardAssistantMessageAsync(
                turn.UserAor,
                turn.AssistantNumber,
                turn.AssistantMessageId,
                token), isTerminal: true, cancellationToken);
        }

        public async Task FlushTurnAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken)
        {
            await turn.Completion.Task.WaitAsync(cancellationToken);
            if (turn.PersistenceException is not null)
            {
                throw new InvalidOperationException($"Turn {turn.TurnId} could not be persisted.", turn.PersistenceException);
            }
        }

        public async Task PersistCompletedOnlineTurnsAsync(ActiveCallContext activeCall, CancellationToken cancellationToken)
        {
            foreach (OfflineDialogueTurn turn in activeCall.AIAgentContext.GetCompletedOnlineTurns())
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                // 保存用户的完整消息
                ConversationMessage userMessage = new ConversationMessage
                {
                    Id = turn.UserMessageId,
                    TurnId = turn.TurnId,
                    UserAor = turn.UserAor,
                    AssistantNumber = turn.AssistantNumber,
                    Role = ConversationRole.User,
                    FullText = turn.UserText,
                    State = DeliveryState.Read,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ReadAt = now,
                };
                await this._telephoneStore.SaveConversationMessageAsync(userMessage, cancellationToken);

                // 保存用户单句类型的消息
                MessageSegment userMessageSegment = new MessageSegment
                {
                    MessageId = turn.UserMessageId,
                    Sequence = 1,
                    TextContent = turn.UserText,
                    CreatedAt = now,
                };
                await this._telephoneStore.SaveMessageSegmentAsync(userMessageSegment, cancellationToken);

                // 保存助手的完整消息
                ConversationMessage assistantMessage = new ConversationMessage
                {
                    Id = turn.AssistantMessageId,
                    TurnId = turn.TurnId,
                    UserAor = turn.UserAor,
                    AssistantNumber = turn.AssistantNumber,
                    Role = ConversationRole.Assistant,
                    FullText = turn.GetFullText(),
                    State = DeliveryState.Read,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ReadAt = now,
                };
                await this._telephoneStore.SaveConversationMessageAsync(assistantMessage, cancellationToken);

                // 保存助手单句类型的消息
                foreach (OfflineDialogueSegment segment in turn.GetAssistantSegments())
                {
                    MessageSegment assistantMessageSegment = new MessageSegment
                    {
                        MessageId = turn.AssistantMessageId,
                        Sequence = segment.Sequence,
                        TextContent = segment.TextContent,
                        AudioPath = segment.AudioPath,
                    };
                    await this._telephoneStore.SaveMessageSegmentAsync(assistantMessageSegment, cancellationToken);
                }

                activeCall.AIAgentContext.MarkCompletedOnlineTurnPersisted(turn);
            }
        }

        public Task StartProactiveCallAsync(DeviceContext device, OfflineDialogueTurn turn)
        {
            if (!device.TryGetActiveRegistration(out RegistrationBinding? registration) ||
                registration is null ||
                !device.TryBeginCallback())
            {
                turn.CompleteProactiveCall(answered: false);
                return Task.CompletedTask;
            }

            _ = this.CallUserAsync(device, registration, turn);
            return Task.CompletedTask;
        }

        public async Task WaitForProactiveCallResultAsync(OfflineDialogueTurn turn, CancellationToken cancellationToken)
        {
            await turn.ProactiveCallResult.Task.WaitAsync(cancellationToken);
        }

        public Task StopProactiveCallAsync(OfflineDialogueTurn turn)
        {
            turn.StopProactiveCall();
            return Task.CompletedTask;
        }

        public async Task PlayAssistantMessageAsync(
            ActiveCallContext activeCall,
            string messageId,
            Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt,
            CancellationToken cancellationToken)
        {
            activeCall.PauseUserAudioInput();
            try
            {
                long nextSequence = 1;
                while (true)
                {
                    ConversationMessage? message = await this._telephoneStore.GetAssistantMessageAsync(
                        activeCall.UserAor,
                        activeCall.DialedNumber!,
                        messageId,
                        cancellationToken);
                    if (message is null)
                    {
                        await Task.Delay(s_generatingMessagePollInterval, cancellationToken);
                        continue;
                    }

                    IReadOnlyList<MessageSegment> segments = await this._telephoneStore.GetMessageSegmentsAsync(messageId, cancellationToken);
                    foreach (MessageSegment segment in segments.Where(segment => segment.Sequence >= nextSequence))
                    {
                        Task<bool> playbackCompleted = activeCall.BeginPromptPlayback();
                        if (playbackCompleted.IsCompleted)
                        {
                            return;
                        }

                        try
                        {
                            bool synthesized = await synthesizePrompt(
                                segment.TextContent,
                                $"callback-{messageId}",
                                $"callback-{segment.Id}",
                                cancellationToken);
                            if (!synthesized || !await playbackCompleted.WaitAsync(cancellationToken))
                            {
                                return;
                            }
                        }
                        finally
                        {
                            activeCall.CompletePromptPlayback(fullyPlayed: false);
                        }

                        nextSequence = segment.Sequence + 1;
                    }

                    if (message.State == DeliveryState.Generating)
                    {
                        await Task.Delay(s_generatingMessagePollInterval, cancellationToken);
                        continue;
                    }

                    if (message.State == DeliveryState.Unread)
                    {
                        await this._telephoneStore.MarkAssistantMessageReadAsync(
                            activeCall.UserAor,
                            activeCall.DialedNumber!,
                            messageId,
                            cancellationToken);
                    }

                    return;
                }
            }
            finally
            {
                if (!activeCall.CallToken.IsCancellationRequested)
                {
                    activeCall.ResumeUserAudioInput();
                    activeCall.DeviceContext.MarkCallConnected(activeCall);
                }
            }
        }

        public async Task StartInitialCallFlowAsync(ActiveCallContext activeCall, Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt, CancellationToken cancellationToken)
        {
            IReadOnlyList<ConversationMessage> messages;
            string assistantNumber = activeCall.DialedNumber
                ?? throw new InvalidOperationException("The call has no assistant number.");
            try
            {
                messages = await this._telephoneStore.GetUnreadAssistantMessagesAsync(activeCall.UserAor, assistantNumber, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.Logger.LogError(exception, "读取通话 {CallId} 的离线留言失败。", activeCall.CallId);
                await this.ContinueInitialCallAsync(activeCall);
                return;
            }

            if (messages.Count == 0)
            {
                this.Logger.LogInformation("通话 {CallId} 没有离线留言，将进行正常的初始通话流程。", activeCall.CallId);
                await this.ContinueInitialCallAsync(activeCall);
                return;
            }
            if (!activeCall.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                return;
            }

            Task playback = this.PlayInitialUnreadAsync(activeCall, messages, synthesizePrompt, lease);
            if (!this._playbacks.TryAdd(activeCall.CallId, playback))
            {
                lease.Dispose();
                return;
            }
            _ = playback.ContinueWith(
                completedTask => this._playbacks.TryRemove(activeCall.CallId, out Task? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async ValueTask EnqueueAsync(
            OfflineDialogueTurn turn,
            Func<CancellationToken, Task> writeAsync,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref this._disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(DefaultOfflineDialogue));
            }

            await this._operations.Writer.WriteAsync(new PersistenceOperation(turn, writeAsync, isTerminal), cancellationToken);
        }

        private async Task ProcessQueueAsync()
        {
            await foreach (PersistenceOperation operation in this._operations.Reader.ReadAllAsync())
            {
                try
                {
                    if (operation.Turn.PersistenceException is null)
                    {
                        await operation.WriteAsync(CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    operation.Turn.RecordPersistenceException(exception);
                    this.Logger.LogError(exception, "保存离线对话 Turn {TurnId} 失败。", operation.Turn.TurnId);
                }
                finally
                {
                    if (operation.IsTerminal)
                    {
                        operation.Turn.Completion.TrySetResult(true);
                    }
                }
            }
        }

        private async Task CallUserAsync(DeviceContext device, RegistrationBinding registration, OfflineDialogueTurn turn)
        {
            SIPUserAgent? userAgent = null;
            VoIPMediaSession? mediaSession = null;
            ActiveCallContext? callbackCall = null;
            bool pipelineReady = false;
            try
            {
                turn.ProactiveCallCancellation.Token.ThrowIfCancellationRequested();
                userAgent = new SIPUserAgent(this._sipTransport, null, false);
                mediaSession = CreateMediaSession();
                bool answered = await userAgent.Call(
                    registration.Contact.ToString(),
                    turn.AssistantNumber,
                    string.Empty,
                    mediaSession,
                    Math.Max(1, this._config.SIPConfig.CallbackTimeoutSeconds))
                    .WaitAsync(turn.ProactiveCallCancellation.Token);
                if (!answered || !device.TryAttachCallbackCallSession(
                    turn.UserAor,
                    turn.AssistantNumber,
                    userAgent,
                    mediaSession,
                    out callbackCall) || callbackCall is null)
                {
                    turn.CompleteProactiveCall(answered: false);
                    return;
                }

                turn.CompleteProactiveCall(answered: true);
                userAgent.OnCallHungup += _ =>
                {
                    device.CloseCallSession(callbackCall);
                    device.EndCallback();
                };
                if (!callbackCall.TryAcquireUse(out IDisposable? callUseLease) || callUseLease is null)
                {
                    return;
                }

                using (callUseLease)
                {
                    if (!await this._functionToolManager.BuildForActiveCallAsync(device) ||
                        !await this._providerManager.BuildForActiveCallAsync(device) ||
                        !await this._handlerManager.BuildForCallbackCallAsync(device, turn.AssistantMessageId))
                    {
                        return;
                    }
                    pipelineReady = true;
                }
            }
            catch (OperationCanceledException) when (turn.ProactiveCallCancellation.IsCancellationRequested)
            {
                turn.CompleteProactiveCall(answered: false);
            }
            catch (Exception exception)
            {
                turn.CompleteProactiveCall(answered: false);
                this.Logger.LogError(exception, "回拨用户 {UserAor} 的离线回复 {MessageId} 失败。", turn.UserAor, turn.AssistantMessageId);
            }
            finally
            {
                if (!pipelineReady)
                {
                    try
                    {
                        userAgent?.Hangup();
                    }
                    finally
                    {
                        try
                        {
                            if (callbackCall is not null)
                            {
                                device.CloseCallSession(callbackCall);
                            }
                        }
                        finally
                        {
                            device.EndCallback();
                            mediaSession?.Close("callback initialization failed");
                        }
                    }
                }
            }
        }

        private static VoIPMediaSession CreateMediaSession()
        {
            AudioEncoder audioEncoder = new(SupportedAudioFormats.SupportedSDPAudioFormat);
            AudioExtrasSource source = new(audioEncoder, new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
            source.RestrictFormats(format => SupportedAudioFormats.SupportedAudioCodecs.Contains(format.Codec));
            return new VoIPMediaSession(new MediaEndPoints { AudioSource = source }) { AcceptRtpFromAny = true };
        }

        private async Task PlayInitialUnreadAsync(
            ActiveCallContext activeCall,
            IReadOnlyList<ConversationMessage> messages,
            Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt,
            IDisposable lease)
        {
            using (lease)
            {
                activeCall.PauseUserAudioInput();
                try
                {
                    using CancellationTokenSource playbackCts = CancellationTokenSource.CreateLinkedTokenSource(activeCall.CallToken);
                    if (!await this.PlayMenuAsync(activeCall, messages.Count, playbackCts.Token))
                    {
                        return;
                    }

                    DtmfKey? selectedKey = await this.WaitForMenuSelectionAsync(activeCall, playbackCts.Token);
                    if (selectedKey == DtmfKey.One)
                    {
                        await this.PlayMessagesAsync(activeCall, messages, synthesizePrompt, playbackCts.Token);
                    }
                    else if (selectedKey == DtmfKey.Two)
                    {
                        await this._telephoneStore.MarkAssistantMessagesReadAsync(
                            activeCall.UserAor,
                            activeCall.DialedNumber!,
                            messages.Select(message => message.Id).ToArray(),
                            playbackCts.Token);
                    }
                }
                catch (OperationCanceledException) when (activeCall.CallToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    this.Logger.LogError(exception, "播放通话 {CallId} 的离线文本留言失败。", activeCall.CallId);
                }
                finally
                {
                    if (!activeCall.CallToken.IsCancellationRequested)
                    {
                        await this.ContinueInitialCallAsync(activeCall);
                    }
                }
            }
        }

        private Task ContinueInitialCallAsync(
            ActiveCallContext activeCall)
        {
            if (activeCall.AIAgentContext.TryStartInitialGreeting())
            {
                return Task.CompletedTask;
            }

            activeCall.ResumeUserAudioInput();
            activeCall.DeviceContext.MarkCallConnected(activeCall);
            return Task.CompletedTask;
        }

        private async Task PlayMessagesAsync(
            ActiveCallContext activeCall,
            IReadOnlyList<ConversationMessage> messages,
            Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt,
            CancellationToken cancellationToken)
        {
            foreach (ConversationMessage message in messages)
            {
                IReadOnlyList<MessageSegment> segments = await this._telephoneStore.GetMessageSegmentsAsync(message.Id, cancellationToken);
                if (segments.Count == 0)
                {
                    segments = [new MessageSegment { MessageId = message.Id, Sequence = 1, TextContent = message.FullText }];
                }

                foreach (MessageSegment segment in segments)
                {
                    Task<bool> playbackCompleted = activeCall.BeginPromptPlayback();
                    if (playbackCompleted.IsCompleted)
                    {
                        return;
                    }

                    try
                    {
                        bool synthesized = await synthesizePrompt(
                            segment.TextContent,
                            $"offline-{message.Id}",
                            $"offline-{segment.Id}",
                            cancellationToken);
                        if (!synthesized || !await playbackCompleted.WaitAsync(cancellationToken))
                        {
                            return;
                        }
                    }
                    finally
                    {
                        activeCall.CompletePromptPlayback(fullyPlayed: false);
                    }
                }

                await this._telephoneStore.MarkAssistantMessageReadAsync(
                    message.UserAor,
                    message.AssistantNumber,
                    message.Id,
                    cancellationToken);
                if (!ReferenceEquals(message, messages[^1]))
                {
                    await Task.Delay(s_messagePlaybackInterval, cancellationToken);
                }
            }
        }

        private async Task<bool> PlayMenuAsync(ActiveCallContext activeCall, int messageCount, CancellationToken cancellationToken)
        {
            IAudioProcessor? audioProcessor = activeCall.AIAgentContext.PrivateProvider.AudioProcessor;
            if (audioProcessor is null)
            {
                return false;
            }

            this.Logger.LogInformation("通话 {CallId} 有 {MessageCount} 条离线留言，将播放菜单提示。", activeCall.CallId, messageCount);

            activeCall.MarkPlayingPrompt();
            return await audioProcessor.PlayCachedPromptAsync(activeCall, GetMenuAudioFiles(messageCount), cancellationToken);
        }

        private async Task<DtmfKey?> WaitForMenuSelectionAsync(ActiveCallContext activeCall, CancellationToken cancellationToken)
        {
            IDtmfInput? dtmfInput = activeCall.AIAgentContext.PrivateProvider.DtmfInput;
            if (dtmfInput is null)
            {
                return null;
            }

            return await dtmfInput.WaitForDtmfKeyAsync(
                activeCall,
                DtmfKey.One | DtmfKey.Two,
                TimeSpan.FromSeconds(DtmfTimeoutSeconds),
                cancellationToken);
        }

        internal static IReadOnlyList<string> GetMenuAudioFiles(int messageCount) =>
            messageCount is >= 1 and <= 9
                ? ["prompt/offline_message_less_part_1.mp3", $"numbers/{messageCount}.mp3", "prompt/offline_message_less_part_2.mp3"]
                : ["prompt/offline_message_many.mp3"];

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) != 0)
            {
                return;
            }

            this._operations.Writer.TryComplete();
        }

        public async ValueTask DisposeAsync()
        {
            this.Dispose();
            if (this._writerTask is not null)
            {
                await this._writerTask;
            }
        }

        private sealed record PersistenceOperation(
            OfflineDialogueTurn Turn,
            Func<CancellationToken, Task> WriteAsync,
            bool IsTerminal);
    }
}
