using Agent.Telephone.Abstractions.Configs;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers
{
    internal interface IAudioProcessor : IProvider<ModelSetting>
    {
        Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token); 
        Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token);
        Task<byte[]> DecodeFileToPcmWaveAsync(string? path, CancellationToken token = default);
        Task<bool> PlayFileAsync(
            string? path,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken token);
    }
}
