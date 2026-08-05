using System.Collections.Concurrent;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Resources;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.OfflineDialogue
{
    internal sealed class OfflineDialogueProvider : BaseProvider<OfflineDialogueProvider, ModelSetting>, IOfflineDialogue
    {
        private readonly IMessageStore _messageStore;
        private readonly IAudioEditor _audioEditor;
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MessageRecord>> _messagesByTurn = new();
        private readonly ConcurrentDictionary<string, bool> _turnPlaybackResults = new();
        private readonly ConcurrentDictionary<string, byte> _lastTrackedSegments = new();
        private readonly ConcurrentDictionary<string, CancellationTokenRegistration> _callEndRegistrations = new();
        private readonly ConcurrentDictionary<string, Task> _playbacks = new();

        public OfflineDialogueProvider(
            IMessageStore messageStore,
            IAudioEditor audioEditor,
            ILogger<OfflineDialogueProvider> logger)
            : base(logger)
        {
            this._messageStore = messageStore;
            this._audioEditor = audioEditor;
        }

        public override string ProviderType => "offline-dialogue";

        public override string ModelName => nameof(OfflineDialogueProvider);

        public override bool Build(ModelSetting settings) => true;

        public async Task TrackGeneratedAudioAsync(
            ActiveCallContext activeCall,
            long turnId,
            string sentenceId,
            bool isLastSegment,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sentenceId))
            {
                return;
            }

            string turnKey = GetTurnKey(activeCall, turnId);
            if (isLastSegment)
            {
                this._lastTrackedSegments.TryAdd(turnKey, 0);
            }

            string? audioPath = activeCall.AIAgentContext.PrivateProvider.Tts
                ?.GetSavedAudioFilePath(sentenceId);
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                if (isLastSegment &&
                    this._turnPlaybackResults.TryGetValue(turnKey, out bool completedPlayback) &&
                    completedPlayback)
                {
                    this.RemoveTracking(turnKey);
                }
                return;
            }

            string assistantNumber = activeCall.DialedNumber
                ?? throw new InvalidOperationException("The call has no assistant number.");
            this._callEndRegistrations.GetOrAdd(
                activeCall.CallId,
                _ => activeCall.CallToken.Register(() => this.RemoveCallTracking(activeCall.CallId)));
            var messages = this._messagesByTurn.GetOrAdd(
                turnKey,
                _ => new ConcurrentDictionary<string, MessageRecord>(StringComparer.OrdinalIgnoreCase));
            if (!messages.TryAdd(audioPath, new MessageRecord
                {
                    TurnId = turnKey,
                    UserAor = activeCall.UserAor,
                    AssistantNumber = assistantNumber,
                    AudioPath = Path.GetFullPath(audioPath)
                }))
            {
                return;
            }

            MessageRecord message = messages[audioPath];
            try
            {
                message = await this._messageStore.SaveAsync(message, cancellationToken)
                    .ConfigureAwait(false);
                messages[audioPath] = message;
            }
            catch
            {
                messages.TryRemove(audioPath, out _);
                throw;
            }
            if (this._turnPlaybackResults.TryGetValue(turnKey, out bool fullyPlayed) && fullyPlayed)
            {
                await this._messageStore.MarkReadAsync(
                    message.UserAor,
                    message.AssistantNumber,
                    message.Id,
                    cancellationToken).ConfigureAwait(false);
                if (isLastSegment)
                {
                    this.RemoveTracking(turnKey);
                }
            }
        }

        public async Task MarkTurnPlaybackCompletedAsync(
            ActiveCallContext activeCall,
            long turnId,
            bool fullyPlayed,
            CancellationToken cancellationToken)
        {
            string turnKey = GetTurnKey(activeCall, turnId);
            this._turnPlaybackResults[turnKey] = fullyPlayed;
            if (!this._messagesByTurn.TryGetValue(turnKey, out ConcurrentDictionary<string, MessageRecord>? messages))
            {
                return;
            }

            if (fullyPlayed)
            {
                foreach (MessageRecord message in messages.Values)
                {
                    await this._messageStore.MarkReadAsync(
                        message.UserAor,
                        message.AssistantNumber,
                        message.Id,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            if (!fullyPlayed || this._lastTrackedSegments.ContainsKey(turnKey))
            {
                this.RemoveTracking(turnKey);
            }
        }

        public void StartPlayback(ActiveCallContext activeCall)
        {
            if (!activeCall.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                return;
            }

            Task playback = this.PlayUnreadAsync(activeCall, lease);
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

        private async Task PlayUnreadAsync(ActiveCallContext activeCall, IDisposable lease)
        {
            using (lease)
            using (CancellationTokenSource playbackCts = CancellationTokenSource.CreateLinkedTokenSource(
                activeCall.CallToken,
                this._stopping.Token))
            {
                activeCall.PauseAgentMedia();
                try
                {
                    string assistantNumber = activeCall.DialedNumber
                        ?? throw new InvalidOperationException("The call has no assistant number.");
                    IReadOnlyList<MessageRecord> messages = await this._messageStore.GetUnreadAsync(
                        activeCall.UserAor,
                        assistantNumber,
                        playbackCts.Token).ConfigureAwait(false);
                    foreach (MessageRecord message in messages)
                    {
                        bool played = await this._audioEditor.PlayFileAsync(
                            message.AudioPath,
                            activeCall.VoIPRTP,
                            activeCall.NegotiatedAudioFormat,
                            playbackCts.Token).ConfigureAwait(false);
                        if (!played)
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
                    this.Logger.LogError(
                        exception,
                        "播放通话 {CallId} 的离线音频失败。",
                        activeCall.CallId);
                }
                finally
                {
                    activeCall.ResumeAgentMedia();
                }
            }
        }

        private static string GetTurnKey(ActiveCallContext activeCall, long turnId) =>
            $"{activeCall.CallId}:{turnId}";

        private void RemoveTracking(string turnKey)
        {
            this._messagesByTurn.TryRemove(turnKey, out _);
            this._turnPlaybackResults.TryRemove(turnKey, out _);
            this._lastTrackedSegments.TryRemove(turnKey, out _);
        }

        private void RemoveCallTracking(string callId)
        {
            foreach (string turnKey in this._messagesByTurn.Keys.Where(key =>
                key.StartsWith($"{callId}:", StringComparison.Ordinal)))
            {
                this.RemoveTracking(turnKey);
            }

            this._callEndRegistrations.TryRemove(callId, out _);
        }

        public override void Dispose()
        {
            this._stopping.Cancel();
            foreach (CancellationTokenRegistration registration in this._callEndRegistrations.Values)
            {
                registration.Dispose();
            }
            this._stopping.Dispose();
        }
    }
}
