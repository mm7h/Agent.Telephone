using Agent.Telephone.Abstractions.Configs;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers
{
    internal interface IAudioProcessor : IProvider<ModelSetting>
    {
        event Action<uint, byte[], bool, bool>? OnAudioDataAvailable;
        Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token); 
        Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token);
        Task<bool> PlayFileAsync(
            string? path,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken token);
    }
}
