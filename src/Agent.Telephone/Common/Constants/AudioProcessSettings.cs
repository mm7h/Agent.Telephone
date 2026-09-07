namespace Agent.Telephone.Common.Constants
{
    internal static class AudioProcessSettings
    {
        public const int DefaultPacketTimeMs = 20;
        public const int OutputToModelSampleRate = 16000;
        public const int ModelToInputSampleRate = 24000;
        public const int ModelAudioChannels = 1;
        public const int ModelAudioBitsPerSample = 16;
        public const int StreamingAsrPreRollMilliseconds = 600;
        public const int StreamingAsrMaxQueuedAudioMilliseconds = 10000;
    }
}
