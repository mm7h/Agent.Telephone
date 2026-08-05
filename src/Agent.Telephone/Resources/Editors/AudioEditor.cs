using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using ISIPSorceryAudioCodec = SIPSorcery.Media.AudioEncoder;

namespace Agent.Telephone.Resources.Editors
{
    internal class AudioEditor : BaseResource<AudioEditor, ModelSetting>, IAudioEditor
    {
        private readonly ISIPSorceryAudioCodec _audioCodec;
        private readonly IAudioFileEncoder _audioFileEncoder;
        private readonly IAudioFileCaching _audioFileCaching;

        private AudioFormat _supportedAudioFormat = AudioFormat.Empty;


        private const string END_OF_FILE = "End of file";
        private const int DefaultSampleRate = 16000;
        private const int DefaultChannels = 1;
        private const int DefaultBitRate = 128000;
        private const int CachedAudioSampleRate = 8000;
        private const int CachedAudioBytesPerSample = sizeof(short);
        private const int PlaybackPacketDurationMs = 20;
        private const int CachedAudioSamplesPerPacket = CachedAudioSampleRate * PlaybackPacketDurationMs / 1000;
        private const int CachedAudioBytesPerPacket = CachedAudioSamplesPerPacket * CachedAudioBytesPerSample;

        public AudioEditor(ISIPSorceryAudioCodec audioCodec, IAudioFileEncoder audioFileEncoder, IAudioFileCaching audioFileCaching, ILogger<AudioEditor> logger) : base(logger)
        {
            this._audioCodec = audioCodec;
            this._audioFileEncoder = audioFileEncoder;
            this._audioFileCaching = audioFileCaching;
        }

        public event Action<uint, byte[], bool, bool>? OnAudioDataAvailable;


        public override string ResourceName => nameof(AudioEditor);

        public override bool Load(ModelSetting settings)
        {
            this._supportedAudioFormat = SupportedAudioFormats.SupportedSDPAudioFormat.First(format => format.Codec == AudioCodecsEnum.PCMU);

            return true;
        }
        public async Task<bool> SaveAudioFileAsync(string filePath, float[] data)
        {
            return await this.SaveAudioFileAsync(filePath, data, DefaultSampleRate, DefaultChannels, DefaultBitRate);
        }

        public async Task<bool> SaveAudioFileAsync(string filePath, float[] data, int sampleRate, int channels, int bitRate)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            }

            if (data is null || data.Length == 0)
            {
                throw new ArgumentException("Audio data cannot be null or empty.", nameof(data));
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentException("Sample rate must be greater than 0.", nameof(sampleRate));
            }

            if (channels <= 0)
            {
                throw new ArgumentException("Channels must be greater than 0.", nameof(channels));
            }

            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return await this._audioFileEncoder.EncodeAudioFileAsync(filePath, data, sampleRate, channels, bitRate);
        }

        public async Task<bool> SaveAudioFileAsync(string filePath, byte[] pcmData)
        {
            return await this.SaveAudioFileAsync(filePath, pcmData, DefaultSampleRate, DefaultChannels, DefaultBitRate);
        }

        public async Task<bool> SaveAudioFileAsync(string filePath, byte[] pcmData, int sampleRate, int channels, int bitRate)
        {
            if (pcmData is null || pcmData.Length == 0)
            {
                throw new ArgumentException("PCM data cannot be null or empty.", nameof(pcmData));
            }

            if (pcmData.Length % 2 != 0)
            {
                throw new ArgumentException("PCM data length must be even (16-bit samples).", nameof(pcmData));
            }

            float[] floatData = ConvertS16LEToFloat(pcmData);
            return await this.SaveAudioFileAsync(filePath, floatData, sampleRate, channels, bitRate);
        }

        public Task<bool> PlaySIPCodeAudioAsync(SIPResponseStatusCodesEnum sipCode, CancellationToken cancellationToken)
        {
            if (!this.TryGetCachedAudio(sipCode, out byte[]? audioBytes) || audioBytes is null)
            {
                return Task.FromResult(false);
            }

            return this.PlayAudioAsync(audioBytes, cancellationToken);
        }

        public async Task<bool> PlaySIPCodeAudioLoopAsync(SIPResponseStatusCodesEnum sipCode, CancellationToken cancellationToken)
        {
            if (!this.TryGetCachedAudio(sipCode, out byte[]? audioBytes) || audioBytes is null)
            {
                return false;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await this.PlayAudioAsync(audioBytes, cancellationToken))
                {
                    return false;
                }
            }

            return false;
        }

        public Task<bool> PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            if (!this.TryGetCachedAudio(sipCode, out byte[]? audioBytes) || audioBytes is null)
            {
                return Task.FromResult(false);
            }

            return this.PlayAudioAsync(audioBytes, mediaSession, audioFormat, cancellationToken);
        }

        public async Task<bool> PlaySIPCodeAudioLoopAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            if (!this.TryGetCachedAudio(sipCode, out byte[]? audioBytes) || audioBytes is null)
            {
                return false;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await this.PlayAudioAsync(
                        audioBytes,
                        mediaSession,
                        audioFormat,
                        cancellationToken))
                {
                    return false;
                }
            }

            return false;
        }

        public Task<bool> PlayFileAsync(
            string? path,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                this.Logger.LogWarning("无法播放音频文件，路径为空或文件不存在：{Path}", path);
                return Task.FromResult(false);
            }

            if (audioFormat.IsEmpty() ||
                !SupportedAudioFormats.SupportedAudioCodecs.Contains(audioFormat.Codec))
            {
                this.Logger.LogWarning(
                    "无法播放音频文件 {Path}，通话未协商 PCMU/PCMA。",
                    path);
                return Task.FromResult(false);
            }

            return this.PlayDecodedFileAsync(path, mediaSession, audioFormat, cancellationToken);
        }

        public async Task<bool> PlayAudioFileAsync(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                this.Logger.LogWarning("无法播放音频文件，路径为空或文件不存在：{Path}", filePath);
                return false;
            }

            if (this._supportedAudioFormat.IsEmpty())
            {
                this.Logger.LogWarning("无法播放音频文件 {Path}，通话未协商 PCMU/PCMA。", filePath);
                return false;
            }

            FFmpegFileSource source = new FFmpegFileSource(filePath, false, this._audioCodec, 160, false);
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            object pendingFrameLock = new object();
            uint pendingDuration = 0;
            byte[]? pendingFrame = null;
            bool isFirstFrame = true;

            void SendPendingFrame(bool isLastFrame)
            {
                if (pendingFrame is null)
                {
                    return;
                }

                this.OnAudioDataAvailable?.Invoke(
                    pendingDuration,
                    pendingFrame,
                    isFirstFrame,
                    isLastFrame);
                isFirstFrame = false;
                pendingFrame = null;
            }

            void OnRawSample(AudioSamplingRatesEnum samplingRate, uint _, short[] sample)
            {
                try
                {
                    lock (pendingFrameLock)
                    {
                        if (completion.Task.IsCompleted)
                        {
                            return;
                        }

                        SendPendingFrame(isLastFrame: false);
                        short[] resampled = PcmResampler.Resample(
                            sample,
                            (int)samplingRate,
                            CachedAudioSampleRate);
                        pendingDuration = (uint)resampled.Length;
                        pendingFrame = new byte[resampled.Length * CachedAudioBytesPerSample];
                        Buffer.BlockCopy(resampled, 0, pendingFrame, 0, pendingFrame.Length);
                    }
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }

            void OnSourceError(string error)
            {
                if (string.Equals(error, END_OF_FILE, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        lock (pendingFrameLock)
                        {
                            SendPendingFrame(isLastFrame: true);
                        }

                        completion.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                }
                else
                {
                    completion.TrySetException(
                        new InvalidOperationException($"FFmpeg audio source failed: {error}"));
                }
            }

            source.OnAudioSourceRawSample += OnRawSample;
            source.OnAudioSourceError += OnSourceError;
            try
            {
                source.RestrictFormats(format => format.Codec == this._supportedAudioFormat.Codec);
                source.SetAudioSourceFormat(this._supportedAudioFormat);
                await source.StartAudio();
                return await completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "播放音频文件 {Path} 失败。", filePath);
                return false;
            }
            finally
            {
                source.OnAudioSourceRawSample -= OnRawSample;
                source.OnAudioSourceError -= OnSourceError;
                try
                {
                    await source.CloseAudio();
                }
                catch (Exception exception)
                {
                    this.Logger.LogDebug(exception, "关闭 FFmpeg 音频源 {Path} 时发生异常。", filePath);
                }
                source.Dispose();
            }
        }

        private bool TryGetCachedAudio(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes)
        {
            if (!this._audioFileCaching.TryGetAudioBytes(sipCode, out audioBytes)
                || audioBytes is null
                || audioBytes.Length == 0)
            {
                this.Logger.LogWarning("SIP码 {SipCode} 没有可播放的缓存音频。", sipCode);
                return false;
            }

            return true;
        }

        private async Task<bool> PlayAudioAsync(byte[] audioBytes, CancellationToken cancellationToken)
        {
            try
            {
                for (int offset = 0; offset < audioBytes.Length; offset += CachedAudioBytesPerPacket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frameLength = Math.Min(CachedAudioBytesPerPacket, audioBytes.Length - offset);
                    byte[] frame = audioBytes.AsSpan(offset, frameLength).ToArray();
                    uint duration = (uint)(frameLength / CachedAudioBytesPerSample);
                    bool isFirst = offset == 0;
                    bool isLast = offset + frameLength == audioBytes.Length;
                    this.OnAudioDataAvailable?.Invoke(duration, frame, isFirst, isLast);

                    TimeSpan frameDuration = TimeSpan.FromSeconds(
                        (double)duration / CachedAudioSampleRate);
                    await Task.Delay(frameDuration, cancellationToken);
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "播放缓存音频失败。");
                return false;
            }
        }

        private async Task<bool> PlayAudioAsync(
            byte[] audioBytes,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            try
            {
                for (int offset = 0; offset < audioBytes.Length; offset += CachedAudioBytesPerPacket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frameLength = Math.Min(CachedAudioBytesPerPacket, audioBytes.Length - offset);

                    short[] samples = new short[frameLength / CachedAudioBytesPerSample];
                    Buffer.BlockCopy(audioBytes, offset, samples, 0, frameLength);
                    short[] resampled = PcmResampler.Resample(
                        samples,
                        CachedAudioSampleRate,
                        audioFormat.ClockRate);
                    byte[] encoded = this._audioCodec.EncodeAudio(resampled, audioFormat);
                    mediaSession.SendAudio((uint)resampled.Length, encoded);

                    TimeSpan frameDuration = TimeSpan.FromSeconds(
                        (double)resampled.Length / audioFormat.ClockRate);
                    await Task.Delay(frameDuration, cancellationToken);
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "播放缓存音频到通话媒体会话失败。");
                return false;
            }
        }

        private async Task<bool> PlayDecodedFileAsync(
            string filePath,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            FFmpegFileSource source = new(filePath, false, this._audioCodec, 160, false);
            TaskCompletionSource<bool> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnEncodedSample(uint duration, byte[] sample)
            {
                try
                {
                    mediaSession.SendAudio(duration, sample);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }

            void OnSourceError(string error)
            {
                if (string.Equals(error, END_OF_FILE, StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetResult(true);
                }
                else
                {
                    completion.TrySetException(
                        new InvalidOperationException($"FFmpeg audio source failed: {error}"));
                }
            }

            source.OnAudioSourceEncodedSample += OnEncodedSample;
            source.OnAudioSourceError += OnSourceError;
            try
            {
                source.RestrictFormats(format => format.Codec == audioFormat.Codec);
                source.SetAudioSourceFormat(audioFormat);
                await source.StartAudio();
                return await completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "播放音频文件 {Path} 失败。", filePath);
                return false;
            }
            finally
            {
                source.OnAudioSourceEncodedSample -= OnEncodedSample;
                source.OnAudioSourceError -= OnSourceError;
                try
                {
                    await source.CloseAudio();
                }
                catch (Exception exception)
                {
                    this.Logger.LogDebug(
                        exception,
                        "关闭 FFmpeg 音频源 {Path} 时发生异常。",
                        filePath);
                }
                source.Dispose();
            }
        }


        /// <summary>
        /// Convert 16-bit signed little-endian PCM bytes to float array [-1.0, 1.0]
        /// </summary>
        private static float[] ConvertS16LEToFloat(byte[] pcmBytes)
        {
            int sampleCount = pcmBytes.Length / 2;
            float[] floatData = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
                floatData[i] = sample / 32768f;
            }

            return floatData;
        }

        public override void Dispose()
        {

        }
    }
}
