using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.ASR;

namespace Agent.Telephone.Providers
{
    internal interface IAsr : IProvider<ModelSetting>
    {
        void RegisterDevice(ActiveCallContext activeCall, IAsrEventCallback callback);
        Task ConvertSpeechTextAsync(Workflow<float[]> workflow, int sampleRate, CancellationToken token);
    }
}
