using Agent.Telephone.Providers.ASR.Sherpa;
using Agent.Telephone.Providers.TTS.Sherpa;
using Agent.Telephone.Providers.VAD.Sherpa;

namespace Agent.Telephone.Common.Constants
{
    internal static class SherpaModels
    {
        public static readonly string[] VadModels = [nameof(Silero)];
        public static readonly string[] AsrModels = [nameof(SenseVoice), nameof(Paraformer)];
        public static readonly string[] TtsModels = [nameof(Kokoro)];
    }
}
