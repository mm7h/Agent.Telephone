using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace Agent.Telephone.Resources.Audio
{
    /// <summary>
    /// Decodes a file through SIPSorceryMedia.FFmpeg and sends negotiated
    /// PCMU/PCMA frames to an existing media session.
    /// </summary>
    internal sealed class FfmpegAudioPlayer : BaseResource<FfmpegAudioPlayer, ModelSetting>
    {
        private const string END_OF_FILE = "End of file";
        public FfmpegAudioPlayer(ILogger<FfmpegAudioPlayer> logger)
            : base(logger)
        {
        }

        public override string ResourceName => nameof(FfmpegAudioPlayer);

        public override bool Load(ModelSetting settings)
        {
            return true;
        }

        public async Task<byte[]> DecodeFileToPcmWaveAsync(string? path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return [];
            }

            AudioEncoder encoder = new(SupportedAudioFormats.SupportedSDPAudioFormat);
            FFmpegFileSource source = new(path, false, encoder, 160, false);
            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            List<short> samples = [];
            object sampleLock = new();
            int sampleRate = (int)AudioSamplingRatesEnum.Rate8KHz;

            void OnRawSample(
                AudioSamplingRatesEnum samplingRate,
                uint duration,
                short[] sample)
            {
                lock (sampleLock)
                {
                    sampleRate = (int)samplingRate;
                    samples.AddRange(sample);
                }
            }

            void OnSourceError(string error)
            {
                if (string.Equals(error, END_OF_FILE, StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(
                        new InvalidOperationException(
                            $"FFmpeg audio source failed: {error}"));
                }
            }

            source.OnAudioSourceRawSample += OnRawSample;
            source.OnAudioSourceError += OnSourceError;
            try
            {
                AudioFormat pcmu = SupportedAudioFormats.SupportedSDPAudioFormat
                    .First(format => format.Codec == AudioCodecsEnum.PCMU);
                source.RestrictFormats(format => format.Codec == AudioCodecsEnum.PCMU);
                source.SetAudioSourceFormat(pcmu);
                await source.StartAudio().ConfigureAwait(false);
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (sampleLock)
                {
                    return PcmWaveHelper.CreateMono16BitWave(samples, sampleRate);
                }
            }
            finally
            {
                source.OnAudioSourceRawSample -= OnRawSample;
                source.OnAudioSourceError -= OnSourceError;
                try
                {
                    await source.CloseAudio().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    this.Logger.LogDebug(exception, "关闭 FFmpeg PCM 解码源 {Path} 时发生异常。", path);
                }
                source.Dispose();
            }
        }

        public async Task<bool> PlayFileAsync(string? path, VoIPMediaSession mediaSession, AudioFormat audioFormat, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                this.Logger.LogWarning("无法播放音频文件，路径为空或文件不存在：{Path}", path);
                return false;
            }

            if (audioFormat.IsEmpty() ||
                !SupportedAudioFormats.SupportedAudioCodecs.Contains(audioFormat.Codec))
            {
                this.Logger.LogWarning("无法播放音频文件 {Path}，通话未协商 PCMU/PCMA。", path);
                return false;
            }

            AudioEncoder encoder = new(SupportedAudioFormats.SupportedSDPAudioFormat);
            FFmpegFileSource source = new(path, false, encoder, 160, false);
            TaskCompletionSource<bool> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

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
                await source.StartAudio().ConfigureAwait(false);
                return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "播放音频文件 {Path} 失败。", path);
                return false;
            }
            finally
            {
                source.OnAudioSourceEncodedSample -= OnEncodedSample;
                source.OnAudioSourceError -= OnSourceError;
                try
                {
                    await source.CloseAudio().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    this.Logger.LogDebug(exception, "关闭 FFmpeg 音频源 {Path} 时发生异常。", path);
                }
                source.Dispose();
            }
        }

        public override void Dispose()
        {
        }
    }
}
