using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.ASR;
using Agent.Telephone.Providers.ASR.Contexts;

namespace Agent.Telephone.Providers
{
    internal interface IAsr : IProvider<ModelSetting>
    {
        bool IsStreaming { get; }
        void RegisterDevice(ActiveCallContext activeCall, IAsrEventCallback callback);
        Task ConvertSpeechTextAsync(Workflow<float[]> workflow, int sampleRate, CancellationToken token);
        Task ConvertSpeechTextStreamingAsync(
            Workflow<float[]> workflow,
            int sampleRate,
            StreamingAsrOperation operation,
            CancellationToken token);
    }
}
