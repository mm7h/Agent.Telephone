using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Resources
{
    internal interface IAudioPromptPlayer : IDisposable
    {
        Task<bool> PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken);

        Task<bool> PlaySIPCodeAudioLoopAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken);

        Task<bool> PlayCachedAudioFilesAsync(
            IReadOnlyList<string> relativeFilePaths,
            int outputSampleRate,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken);

        Task<bool> PlayFileAsync(
            string filePath,
            int outputSampleRate,
            int packetTimeMs,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken);
    }
}
