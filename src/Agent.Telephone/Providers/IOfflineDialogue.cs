using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers
{
    internal interface IOfflineDialogue : IProvider<ModelSetting>
    {
        Task TrackGeneratedAudioAsync(
            ActiveCallContext activeCall,
            long turnId,
            string sentenceId,
            bool isLastSegment,
            CancellationToken cancellationToken);

        Task MarkTurnPlaybackCompletedAsync(
            ActiveCallContext activeCall,
            long turnId,
            bool fullyPlayed,
            CancellationToken cancellationToken);

        void StartPlayback(ActiveCallContext activeCall);
    }
}
