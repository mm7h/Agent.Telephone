using SherpaOnnx;

namespace Agent.Telephone.Providers.ASR.Contexts
{
    internal record AsrRequest(
        string DeviceId,
        OfflineStream Stream,
        int SampleRate,
        long TurnId,
        IAsrEventCallback Callback,
        CancellationToken Token
    );
}
