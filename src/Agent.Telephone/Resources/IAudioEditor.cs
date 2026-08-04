using Agent.Telephone.Abstractions.Configs;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Resources
{
    internal interface IAudioEditor : IResource<ModelSetting>
    {
        event Action<uint, byte[], bool, bool>? OnAudioDataAvailable;

        /// <summary>
        /// Save float audio data to file with default settings (16kHz, mono, 128kbps)
        /// </summary>
        /// <param name="filePath">Output file path (format determined by extension)</param>
        /// <param name="data">Audio samples normalized to [-1.0, 1.0]</param>
        Task<bool> SaveAudioFileAsync(string filePath, float[] data);

        /// <summary>
        /// Save float audio data to file with specified settings
        /// </summary>
        /// <param name="filePath">Output file path (format determined by extension)</param>
        /// <param name="data">Audio samples normalized to [-1.0, 1.0]</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="bitRate">Bit rate for encoding</param>
        Task<bool> SaveAudioFileAsync(string filePath, float[] data, int sampleRate, int channels, int bitRate);

        /// <summary>
        /// Save 16-bit signed little-endian PCM data to file with default settings (16kHz, mono, 128kbps)
        /// </summary>
        /// <param name="filePath">Output file path (format determined by extension)</param>
        /// <param name="pcmData">Raw 16-bit signed little-endian PCM bytes</param>
        Task<bool> SaveAudioFileAsync(string filePath, byte[] pcmData);

        /// <summary>
        /// Save 16-bit signed little-endian PCM data to file with specified settings
        /// </summary>
        /// <param name="filePath">Output file path (format determined by extension)</param>
        /// <param name="pcmData">Raw 16-bit signed little-endian PCM bytes</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="bitRate">Bit rate for encoding</param>
        Task<bool> SaveAudioFileAsync(string filePath, byte[] pcmData, int sampleRate, int channels, int bitRate);
        Task<bool> PlaySIPCodeAudioAsync(SIPResponseStatusCodesEnum sipCode, CancellationToken cancellationToken);
        Task<bool> PlaySIPCodeAudioLoopAsync(SIPResponseStatusCodesEnum sipCode, CancellationToken cancellationToken);
        Task<bool> PlayAudioFileAsync(string filePath, CancellationToken cancellationToken);

        /// <summary>
        /// Plays the cached audio for a SIP response code directly to the
        /// negotiated media session (encoded to the call's audio format).
        /// </summary>
        Task<bool> PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken);

        /// <summary>
        /// Loops the cached audio for a SIP response code to the negotiated
        /// media session until the cancellation token is cancelled.
        /// </summary>
        Task<bool> PlaySIPCodeAudioLoopAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken);

        /// <summary>
        /// Decodes a file and plays it directly to the negotiated media session.
        /// </summary>
        Task<bool> PlayFileAsync(
            string? path,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken);
    }
}
