using SIPSorcery.SIP;

namespace Agent.Telephone.Resources
{
    internal interface IAudioFileCaching : IResource<IDictionary<int, string>>
    {
        bool TryGetAudioBytes(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes);
    }
}
