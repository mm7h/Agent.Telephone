namespace Agent.Telephone.Media.Encoders
{
    internal interface IAudioEncoder
    {
        Task<bool> EncodeAsync(string outputPath, float[] audioData, int sampleRate, int channels, int bitRate = 128000);
    }
}
