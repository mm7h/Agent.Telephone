using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Common.Constants
{
    internal static class SupportedAudioFormats
    {
        public static readonly AudioFormat[] SupportedSDPAudioFormat = [
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), 
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)
        ];

        public static readonly AudioCodecsEnum[] SupportedAudioCodecs = [
            AudioCodecsEnum.PCMU, 
            AudioCodecsEnum.PCMA
        ];
    }
}
