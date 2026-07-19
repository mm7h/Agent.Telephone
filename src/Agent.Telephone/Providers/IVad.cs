using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Providers.VAD;

namespace Agent.Telephone.Providers
{
    internal interface IVad : IProvider<ModelSetting>
    {
        int FrameSize { get; }

        void RegisterDevice(string deviceId, IVadEventCallback callback);

        Task AnalysisVoiceAsync(string deviceId, float[] audioData, CancellationToken token);

        void ResetSessionState(string deviceId);
    }
}
