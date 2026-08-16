using SIPSorcery.SIP;

namespace Agent.Telephone.Resources
{
    internal interface IAudioFileCaching : IDisposable
    {
        bool Load();

        bool TryGetAudioBytes(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes);

        bool TryGetAudioBytes(string relativeFilePath, out byte[]? audioBytes);
    }
}
