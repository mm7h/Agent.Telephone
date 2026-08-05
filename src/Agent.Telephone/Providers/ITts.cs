using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.TTS;

namespace Agent.Telephone.Providers
{
    internal interface ITts : IProvider<ModelSetting>
    {
        void RegisterDevice(string deviceId, ITtsEventCallback callback);
        Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token);
        string? GetSavedAudioFilePath(string sentenceId);
    }
}
