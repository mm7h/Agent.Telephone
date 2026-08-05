using Agent.Telephone.Common.BuildConfigs;
using SIPSorcery.SIP;

namespace Agent.Telephone.Resources
{
    internal interface IAudioFileCaching : IResource<AudioFileCachingBuildConfig>
    {
        bool TryGetAudioBytes(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes);
    }
}
