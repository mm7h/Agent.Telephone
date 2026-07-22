using Agent.Telephone.Abstractions.Configs;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers
{
    internal interface IAudioProcessor : IProvider<ModelSetting>
    {
        Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token); 
        Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token);
    }
}
