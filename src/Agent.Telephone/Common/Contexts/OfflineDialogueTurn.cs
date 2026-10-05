using System.Text;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class OfflineDialogueTurn
    {
        private readonly object _persistenceLock = new();
        private readonly List<OfflineDialogueSegment> _segments = [];

        public OfflineDialogueTurn(
            string turnId,
            string userAor,
            string assistantNumber,
            string userMessageId,
            string assistantMessageId,
            string userText)
        {
            this.TurnId = turnId;
            this.UserAor = userAor;
            this.AssistantNumber = assistantNumber;
            this.UserMessageId = userMessageId;
            this.AssistantMessageId = assistantMessageId;
            this.UserText = userText;
        }

        public string TurnId { get; }
        public string UserAor { get; }
        public string AssistantNumber { get; }
        public string UserMessageId { get; }
        public string AssistantMessageId { get; }
        public string UserText { get; }
        public bool IsOfflineDelivery { get; set; }
        public StringBuilder FullText { get; } = new();
        public Exception? PersistenceException { get; private set; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource ProactiveCallCancellation { get; } = new();
        public TaskCompletionSource<bool> ProactiveCallResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsPersistenceStarted { get; private set; }

        public OfflineDialogueSegment AddAssistantSegment(string textContent, string? audioPath)
        {
            lock (this._persistenceLock)
            {
                var segment = new OfflineDialogueSegment(this._segments.Count + 1, textContent, audioPath);
                this._segments.Add(segment);
                this.FullText.Append(textContent);
                return segment;
            }
        }

        public bool TryBeginPersistence(out IReadOnlyList<OfflineDialogueSegment> segments)
        {
            lock (this._persistenceLock)
            {
                if (this.IsPersistenceStarted)
                {
                    segments = [];
                    return false;
                }

                this.IsPersistenceStarted = true;
                segments = this._segments.ToArray();
                return true;
            }
        }

        public IReadOnlyList<OfflineDialogueSegment> GetAssistantSegments()
        {
            lock (this._persistenceLock)
            {
                return this._segments.ToArray();
            }
        }

        public string GetFullText()
        {
            lock (this._persistenceLock)
            {
                return this.FullText.ToString();
            }
        }
        public void RecordPersistenceException(Exception exception) => this.PersistenceException ??= exception;
        public void CompleteProactiveCall(bool answered) => this.ProactiveCallResult.TrySetResult(answered);

        public void StopProactiveCall()
        {
            this.ProactiveCallCancellation.Cancel();
            this.CompleteProactiveCall(answered: false);
        }
    }

    internal sealed record OfflineDialogueSegment(long Sequence, string TextContent, string? AudioPath);
}
