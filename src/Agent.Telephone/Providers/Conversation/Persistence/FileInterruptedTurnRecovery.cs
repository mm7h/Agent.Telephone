using Agent.Telephone.Abstractions.Persistence;

namespace Agent.Telephone.Providers.Conversation.Persistence
{
    public sealed class FileInterruptedTurnRecovery : IInterruptedTurnRecovery
    {
        private readonly ITurnStore _turnStore;
        private readonly IMessageStore _messageStore;

        public FileInterruptedTurnRecovery(ITurnStore turnStore, IMessageStore messageStore)
        {
            _turnStore = turnStore ?? throw new ArgumentNullException(nameof(turnStore));
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        }

        public async Task<int> RecoverAsync(
            string interruptionText,
            ReadOnlyMemory<byte> interruptionWave = default,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(interruptionText))
            {
                throw new ArgumentException("Interruption message cannot be empty.", nameof(interruptionText));
            }

            var runningTurns = await _turnStore
                .GetByStateAsync(TurnState.Running, cancellationToken)
                .ConfigureAwait(false);
            var recovered = 0;
            foreach (var turn in runningTurns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                await _messageStore.SaveAsync(
                    new MessageRecord
                    {
                        Id = $"interrupted-{turn.Id}",
                        TurnId = turn.Id,
                        UserAor = turn.UserAor,
                        AssistantNumber = turn.AssistantNumber,
                        Text = interruptionText,
                        State = DeliveryState.Unread,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    interruptionWave,
                    cancellationToken).ConfigureAwait(false);

                await _turnStore.SaveAsync(
                    turn with
                    {
                        State = TurnState.Interrupted,
                        UpdatedAt = now,
                        CompletedAt = now,
                        FailureReason = interruptionText
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                recovered++;
            }

            return recovered;
        }
    }
}
