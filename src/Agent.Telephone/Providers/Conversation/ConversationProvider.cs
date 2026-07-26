using System.Collections.Concurrent;
using System.Text;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.Conversation
{
    internal sealed class ConversationProvider : BaseProvider<ConversationProvider, ModelSetting>
    {
        private readonly ITurnStore _turnStore;
        private readonly IMessageStore _messageStore;
        private readonly IConversationStore _conversationStore;
        private readonly IMessageDeliveryCoordinator _deliveryCoordinator;
        private readonly ConcurrentDictionary<string, RunningTurn> _turnsByCall = new();
        private readonly ConcurrentDictionary<string, RunningTurn> _turnsByConversation = new();

        public ConversationProvider(
            ITurnStore turnStore,
            IMessageStore messageStore,
            IConversationStore conversationStore,
            IMessageDeliveryCoordinator deliveryCoordinator,
            ILogger<ConversationProvider> logger)
            : base(logger)
        {
            this._turnStore = turnStore;
            this._messageStore = messageStore;
            this._conversationStore = conversationStore;
            this._deliveryCoordinator = deliveryCoordinator;
        }

        public override string ProviderType => "conversation";

        public override string ModelName => nameof(ConversationProvider);

        public override bool Build(ModelSetting settings) => true;

        public bool IsRunning(string userAor, string assistantNumber) =>
            this._turnsByConversation.ContainsKey(GetConversationKey(userAor, assistantNumber));

        public async Task BeginAsync(
            ActiveCallContext activeCall,
            string userText,
            CancellationToken cancellationToken)
        {
            string userAor = activeCall.UserAor;
            string assistantNumber = activeCall.DialedNumber
                ?? throw new InvalidOperationException("The call has no assistant number.");
            string conversationKey = GetConversationKey(userAor, assistantNumber);

            if (this._turnsByConversation.TryRemove(conversationKey, out RunningTurn? previous))
            {
                this._turnsByCall.TryRemove(previous.CallKey, out _);
                await this.CancelAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            if (!activeCall.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                throw new InvalidOperationException("The call is ending and cannot start another turn.");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TurnRecord record = new()
            {
                UserAor = userAor,
                AssistantNumber = assistantNumber,
                UserText = userText,
                State = TurnState.Running,
                StartedAt = now,
                UpdatedAt = now
            };
            RunningTurn running = new(activeCall, record, lease, conversationKey);
            running.RegisterCallEnded(() => this.OnCallEnded(running));
            this._turnsByCall[running.CallKey] = running;
            this._turnsByConversation[conversationKey] = running;

            try
            {
                await this._turnStore.SaveAsync(record, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                this.Remove(running);
                running.Dispose();
                throw;
            }
        }

        public void AppendResponse(ActiveCallContext activeCall, string text)
        {
            this.AppendResponse(activeCall, activeCall.TurnId, text);
        }

        public void AppendResponse(ActiveCallContext activeCall, long turnId, string text)
        {
            if (this.TryGet(activeCall, turnId, out RunningTurn running))
            {
                running.AppendText(text);
            }
        }

        public void AppendAudio(ActiveCallContext activeCall, ReadOnlySpan<float> audio)
        {
            this.AppendAudio(activeCall, activeCall.TurnId, audio);
        }

        public void AppendAudio(
            ActiveCallContext activeCall,
            long turnId,
            ReadOnlySpan<float> audio)
        {
            if (this.TryGet(activeCall, turnId, out RunningTurn running))
            {
                running.AppendAudio(audio);
            }
        }

        public void MarkTextCompleted(ActiveCallContext activeCall, bool hasOutput)
        {
            if (!hasOutput)
            {
                _ = this.CompleteWithoutAudioAsync(
                    activeCall,
                    activeCall.TurnId,
                    CancellationToken.None);
            }
        }

        public Task CompleteWithoutAudioAsync(
            ActiveCallContext activeCall,
            long turnId,
            CancellationToken cancellationToken)
        {
            if (!this.TryGet(activeCall, turnId, out RunningTurn running))
            {
                return Task.CompletedTask;
            }

            running.MarkSynthesisCompleted();
            running.MarkPlaybackEnded(fullyPlayed: true);
            return this.CompleteAsync(running, cancellationToken);
        }

        public Task MarkSynthesisCompletedAsync(
            ActiveCallContext activeCall,
            long turnId,
            CancellationToken cancellationToken)
        {
            if (!this.TryGet(activeCall, turnId, out RunningTurn running))
            {
                return Task.CompletedTask;
            }

            running.MarkSynthesisCompleted();
            if (running.PlaybackEnded)
            {
                _ = this.CompleteAsync(running, cancellationToken);
            }
            return Task.CompletedTask;
        }

        public Task MarkPlaybackEndedAsync(
            ActiveCallContext activeCall,
            long turnId,
            bool fullyPlayed,
            CancellationToken cancellationToken)
        {
            if (!this.TryGet(activeCall, turnId, out RunningTurn running))
            {
                return Task.CompletedTask;
            }

            running.MarkPlaybackEnded(fullyPlayed);
            if (running.SynthesisCompleted)
            {
                _ = this.CompleteAsync(running, cancellationToken);
            }
            return Task.CompletedTask;
        }

        public Task FailAsync(
            ActiveCallContext activeCall,
            string reason,
            CancellationToken cancellationToken)
        {
            return this.FailAsync(
                activeCall,
                activeCall.TurnId,
                reason,
                cancellationToken);
        }

        public Task FailAsync(
            ActiveCallContext activeCall,
            long turnId,
            string reason,
            CancellationToken cancellationToken)
        {
            return this.TryGet(activeCall, turnId, out RunningTurn running)
                ? this.FailAsync(running, reason, cancellationToken)
                : Task.CompletedTask;
        }

        public async Task<string?> BuildRecentMemoryAsync(
            string userAor,
            string assistantNumber,
            int maximumTurns,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ConversationTurn> turns = await this._conversationStore
                .GetRecentAsync(userAor, assistantNumber, maximumTurns, cancellationToken)
                .ConfigureAwait(false);
            if (turns.Count == 0)
            {
                return null;
            }

            StringBuilder memory = new("Recent telephone conversation history:");
            foreach (ConversationTurn turn in turns)
            {
                memory.Append("\nUser: ");
                memory.Append(turn.UserText);
                memory.Append("\nAssistant: ");
                memory.Append(turn.AssistantText);
            }
            return memory.ToString();
        }

        private async Task CompleteAsync(RunningTurn running, CancellationToken cancellationToken)
        {
            if (!running.TryComplete())
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string responseText = running.GetResponseText();
            byte[] wave = running.CreateWave();
            TurnRecord completed = running.Record with
            {
                ResponseText = responseText,
                State = TurnState.Completed,
                UpdatedAt = now,
                CompletedAt = now
            };

            try
            {
                await this._turnStore.SaveAsync(completed, wave, cancellationToken).ConfigureAwait(false);
                await this._conversationStore.SaveAsync(
                    new ConversationTurn
                    {
                        Id = completed.Id,
                        UserAor = completed.UserAor,
                        AssistantNumber = completed.AssistantNumber,
                        UserText = completed.UserText,
                        AssistantText = responseText,
                        CompletedAt = now
                    },
                    cancellationToken).ConfigureAwait(false);

                bool requiresDelivery = !running.FullyPlayed;
                MessageRecord? message = null;
                if (requiresDelivery)
                {
                    message = new MessageRecord
                    {
                        Id = completed.Id,
                        TurnId = completed.Id,
                        UserAor = completed.UserAor,
                        AssistantNumber = completed.AssistantNumber,
                        Text = responseText,
                        State = DeliveryState.PendingCallback,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    await this._messageStore.SaveAsync(message, wave, cancellationToken).ConfigureAwait(false);
                }

                this.Remove(running);
                running.Dispose();

                if (message is not null)
                {
                    bool delivered = await this._deliveryCoordinator
                        .TryDeliverAsync(message, cancellationToken)
                        .ConfigureAwait(false);
                    DateTimeOffset deliveryTime = DateTimeOffset.UtcNow;
                    await this._messageStore.SaveAsync(
                        message with
                        {
                            State = delivered ? DeliveryState.Read : DeliveryState.Unread,
                            UpdatedAt = deliveryTime,
                            ReadAt = delivered ? deliveryTime : null
                        },
                        wave,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "Persisting completed turn {turnId} failed.", completed.Id);
                this.Remove(running);
                running.Dispose();
            }
        }

        private async Task CancelAsync(RunningTurn running, CancellationToken cancellationToken)
        {
            if (!running.TryComplete())
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            try
            {
                await this._turnStore.SaveAsync(
                    running.Record with
                    {
                        State = TurnState.CancelledByUser,
                        UpdatedAt = now,
                        CompletedAt = now
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                running.Dispose();
            }
        }

        private async Task FailAsync(
            RunningTurn running,
            string reason,
            CancellationToken cancellationToken)
        {
            if (!running.TryComplete())
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            try
            {
                await this._turnStore.SaveAsync(
                    running.Record with
                    {
                        ResponseText = running.GetResponseText(),
                        State = TurnState.Failed,
                        UpdatedAt = now,
                        CompletedAt = now,
                        FailureReason = reason
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.Remove(running);
                running.Dispose();
            }
        }

        private bool TryGet(
            ActiveCallContext activeCall,
            long turnId,
            out RunningTurn running)
        {
            bool found = this._turnsByCall.TryGetValue(
                GetCallKey(activeCall, turnId),
                out RunningTurn? candidate);
            running = candidate!;
            return found;
        }

        private void Remove(RunningTurn running)
        {
            this._turnsByCall.TryRemove(running.CallKey, out _);
            if (this._turnsByConversation.TryGetValue(
                    running.ConversationKey,
                    out RunningTurn? current) &&
                ReferenceEquals(current, running))
            {
                this._turnsByConversation.TryRemove(running.ConversationKey, out _);
            }
        }

        private void OnCallEnded(RunningTurn running)
        {
            running.MarkPlaybackEnded(fullyPlayed: false);
            if (running.SynthesisCompleted)
            {
                _ = this.CompleteAsync(running, CancellationToken.None);
            }
        }

        private static string GetCallKey(ActiveCallContext activeCall) =>
            GetCallKey(activeCall, activeCall.TurnId);

        private static string GetCallKey(ActiveCallContext activeCall, long turnId) =>
            $"{activeCall.CallId}:{turnId}";

        private static string GetConversationKey(string userAor, string assistantNumber) =>
            $"{userAor}\n{assistantNumber}".ToUpperInvariant();

        private sealed class RunningTurn : IDisposable
        {
            private readonly object _sync = new();
            private readonly IDisposable _lease;
            private readonly StringBuilder _response = new();
            private readonly List<float> _audio = [];
            private CancellationTokenRegistration _callEndedRegistration;
            private int _completed;
            private int _synthesisCompleted;
            private int _playbackEnded;
            private int _fullyPlayed;

            public RunningTurn(
                ActiveCallContext activeCall,
                TurnRecord record,
                IDisposable lease,
                string conversationKey)
            {
                this.ActiveCall = activeCall;
                this.Record = record;
                this._lease = lease;
                this.ConversationKey = conversationKey;
                this.CallKey = GetCallKey(activeCall);
            }

            public ActiveCallContext ActiveCall { get; }
            public TurnRecord Record { get; }
            public string CallKey { get; }
            public string ConversationKey { get; }
            public bool SynthesisCompleted => Volatile.Read(ref this._synthesisCompleted) != 0;
            public bool PlaybackEnded => Volatile.Read(ref this._playbackEnded) != 0;
            public bool FullyPlayed => Volatile.Read(ref this._fullyPlayed) != 0;

            public void RegisterCallEnded(Action callback)
            {
                this._callEndedRegistration = this.ActiveCall.CallToken.Register(callback);
            }

            public void MarkSynthesisCompleted()
            {
                Interlocked.Exchange(ref this._synthesisCompleted, 1);
            }

            public void MarkPlaybackEnded(bool fullyPlayed)
            {
                if (fullyPlayed)
                {
                    Interlocked.Exchange(ref this._fullyPlayed, 1);
                }
                Interlocked.Exchange(ref this._playbackEnded, 1);
            }

            public void AppendText(string text)
            {
                lock (this._sync)
                {
                    this._response.Append(text);
                }
            }

            public void AppendAudio(ReadOnlySpan<float> audio)
            {
                lock (this._sync)
                {
                    for (int index = 0; index < audio.Length; index++)
                    {
                        this._audio.Add(audio[index]);
                    }
                }
            }

            public string GetResponseText()
            {
                lock (this._sync)
                {
                    return this._response.ToString();
                }
            }

            public byte[] CreateWave()
            {
                lock (this._sync)
                {
                    return PcmWaveHelper.CreateMono16BitWave(
                        this._audio,
                        AudioProcessSettings.ModelToInputSampleRate);
                }
            }

            public bool TryComplete() => Interlocked.Exchange(ref this._completed, 1) == 0;

            public void Dispose()
            {
                this._callEndedRegistration.Dispose();
                this._lease.Dispose();
            }
        }

        public override void Dispose()
        {
            foreach (RunningTurn running in this._turnsByCall.Values)
            {
                this.Remove(running);
                running.Dispose();
            }
        }
    }
}
