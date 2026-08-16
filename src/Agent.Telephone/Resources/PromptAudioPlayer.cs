using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;
using ISIPSorceryAudioCodec = SIPSorcery.Media.AudioEncoder;

namespace Agent.Telephone.Resources
{
    internal sealed class PromptAudioPlayer : IAudioPromptPlayer
    {
        private const int CachedSampleRate = 8000;
        private readonly ISIPSorceryAudioCodec _audioCodec;
        private readonly IAudioFileCaching _audioFileCaching;
        private readonly Func<IUrlAudioPlayer> _audioPlayerFactory;
        private readonly ILogger<PromptAudioPlayer> _logger;

        public PromptAudioPlayer(
            ISIPSorceryAudioCodec audioCodec,
            IAudioFileCaching audioFileCaching,
            Func<IUrlAudioPlayer> audioPlayerFactory,
            ILogger<PromptAudioPlayer> logger)
        {
            this._audioCodec = audioCodec;
            this._audioFileCaching = audioFileCaching;
            this._audioPlayerFactory = audioPlayerFactory;
            this._logger = logger;
        }

        public Task<bool> PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken)
        {
            return !this._audioFileCaching.TryGetAudioBytes(sipCode, out byte[]? audioBytes) ||
                audioBytes is null || audioBytes.Length == 0
                ? Task.FromResult(false)
                : this.PlayCachedAudioAsync(audioBytes, mediaSession, audioFormat, packetTimeMs, cancellationToken);
        }

        public async Task<bool> PlaySIPCodeAudioLoopAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await this.PlaySIPCodeAudioAsync(
                        sipCode,
                        mediaSession,
                        audioFormat,
                        packetTimeMs,
                        cancellationToken))
                {
                    return false;
                }
            }

            return false;
        }

        public Task<bool> PlayCachedAudioFilesAsync(
            IReadOnlyList<string> relativeFilePaths,
            int outputSampleRate,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken)
        {
            if (relativeFilePaths.Count == 0)
            {
                return Task.FromResult(false);
            }

            var audioFiles = new List<byte[]>(relativeFilePaths.Count);
            int totalLength = 0;
            foreach (string relativeFilePath in relativeFilePaths)
            {
                if (!this._audioFileCaching.TryGetAudioBytes(relativeFilePath, out byte[]? audioBytes) ||
                    audioBytes is null ||
                    audioBytes.Length == 0)
                {
                    return Task.FromResult(false);
                }

                audioFiles.Add(audioBytes);
                totalLength += audioBytes.Length;
            }

            byte[] combinedAudio = new byte[totalLength];
            int offset = 0;
            foreach (byte[] audioBytes in audioFiles)
            {
                Buffer.BlockCopy(audioBytes, 0, combinedAudio, offset, audioBytes.Length);
                offset += audioBytes.Length;
            }

            return this.PlayCachedAudioAsync(
                combinedAudio,
                outputSampleRate,
                onAudioData,
                cancellationToken);
        }

        public async Task<bool> PlayFileAsync(
            string filePath,
            int outputSampleRate,
            int packetTimeMs,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) ||
                outputSampleRate <= 0 || packetTimeMs <= 0)
            {
                return false;
            }

            using IUrlAudioPlayer player = this._audioPlayerFactory();
            void OnAudioData(float[] pcmData, bool _, bool __)
            {
                onAudioData(pcmData);
            }

            player.OnAudioDataAvailable += OnAudioData;
            try
            {
                if (!await player.CheckFFmpegInstalledAsync(cancellationToken) ||
                    !await player.LoadAsync(
                        filePath,
                        outputSampleRate,
                        outputChannels: 1,
                        packetTimeMs,
                        cancellationToken))
                {
                    return false;
                }

                await player.PlayAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(exception, "播放音频文件 {Path} 失败。", filePath);
                return false;
            }
            finally
            {
                player.OnAudioDataAvailable -= OnAudioData;
            }
        }

        public void Dispose()
        {
        }

        private async Task<bool> PlayCachedAudioAsync(
            byte[] audioBytes,
            int outputSampleRate,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken)
        {
            if (outputSampleRate <= 0)
            {
                return false;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sampleCount = audioBytes.Length / sizeof(short);
                var source = new short[sampleCount];
                Buffer.BlockCopy(audioBytes, 0, source, 0, sampleCount * sizeof(short));
                short[] resampled = PcmResampler.Resample(source, CachedSampleRate, outputSampleRate);
                onAudioData(resampled.PcmShortToFloat());
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(exception, "播放缓存提示音失败。");
                return false;
            }
        }

        private async Task<bool> PlayCachedAudioAsync(
            byte[] audioBytes,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken)
        {
            if (audioFormat.IsEmpty() || packetTimeMs <= 0 || CachedSampleRate * packetTimeMs % 1000 != 0)
            {
                return false;
            }

            int samplesPerPacket = CachedSampleRate * packetTimeMs / 1000;
            int bytesPerPacket = samplesPerPacket * sizeof(short);
            try
            {
                for (int offset = 0; offset < audioBytes.Length; offset += bytesPerPacket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frameLength = Math.Min(bytesPerPacket, audioBytes.Length - offset);
                    short[] source = new short[frameLength / sizeof(short)];
                    Buffer.BlockCopy(audioBytes, offset, source, 0, frameLength);
                    short[] resampled = PcmResampler.Resample(source, CachedSampleRate, audioFormat.ClockRate);
                    byte[] encoded = this._audioCodec.EncodeAudio(resampled, audioFormat);
                    mediaSession.SendAudio((uint)resampled.Length, encoded);
                    await Task.Delay(
                        TimeSpan.FromSeconds((double)resampled.Length / audioFormat.ClockRate),
                        cancellationToken);
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(exception, "播放缓存提示音失败。");
                return false;
            }
        }
    }
}
