using Agent.Telephone.Abstractions.Configs;

namespace Agent.Telephone.Resources
{
    internal interface IAudioFileEncoder : IResource<ModelSetting>
    {
        Task<bool> EncodeAudioFileAsync(string outputPath, float[] audioData, int sampleRate, int channels, int bitRate = 128000);
    }
}
