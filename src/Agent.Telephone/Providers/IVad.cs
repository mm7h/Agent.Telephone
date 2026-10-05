using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Providers.VAD;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers
{
    internal interface IVad : IProvider<ModelSetting>
    {
        int FrameSize { get; }

        void RegisterDevice(ActiveCallContext activeCall, IVadEventCallback callback);

        Task AnalysisVoiceAsync(
            string deviceId,
            float[] newAudioData,
            float[] bufferedAudioData,
            CancellationToken token);

        void ResetSessionState(string deviceId);
    }
}
