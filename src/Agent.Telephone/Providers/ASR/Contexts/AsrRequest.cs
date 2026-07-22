using SherpaOnnx;

namespace Agent.Telephone.Providers.ASR.Contexts
{
    internal record AsrRequest(
        string DeviceId,
        OfflineStream Stream,
        int SampleRate,
        IAsrEventCallback Callback,
        CancellationToken Token
    );
}
