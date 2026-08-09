using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Media.Abstractions.Dtos;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers
{
    internal interface IAudioProcessor : IProvider<ModelSetting>
    {
        event Action<float[], bool, bool, string?>? OnMixedAudioDataAvailable;

        Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token);
        Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token);
        bool InitializeMixer(int outputSampleRate, int outputChannels, int frameDuration);
        void ProcessAudio(AudioType audioType, float[] audioData, string? sentenceId);
        void CompleteStream(AudioType audioType);
        void ClearAllBuffers();
        void RegisterSubtitle(string sentenceId, AudioType audioType, TtsStatus ttsStatus, string text);
        bool GetSubtitle(string sentenceId, out AudioSubtitle subtitle);
    }
}
