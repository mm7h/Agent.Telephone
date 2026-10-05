using System.Collections.Concurrent;
using System.Buffers;
using System.Buffers.Binary;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Media.Resource;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Resources.AudioFileCaching
{
    internal sealed class DefaultAudioFileCaching : IAudioFileCaching
    {
        private const int CachedSampleRate = 8000;
        private readonly IDictionary<string, byte[]> _audioFileCache =
            new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<SIPResponseStatusCodesEnum, string> _sipAudioFilePaths =
            new ConcurrentDictionary<SIPResponseStatusCodesEnum, string>();
        private readonly Func<IStreamAudioPlayer> _audioPlayerFactory;
        private readonly ILogger<DefaultAudioFileCaching> _logger;

        public DefaultAudioFileCaching(
            Func<IStreamAudioPlayer> audioPlayerFactory,
            ILogger<DefaultAudioFileCaching> logger)
        {
            this._audioPlayerFactory = audioPlayerFactory;
            this._logger = logger;
        }

        public bool Load()
        {
            try
            {
                this._audioFileCache.Clear();
                this._sipAudioFilePaths.Clear();

                foreach (string relativeFilePath in EmbeddedPromptMedia.RelativeFilePaths)
                {
                    this._logger.LogInformation("开始加载音频资源：{FileName}", relativeFilePath);
                    if (!this.CacheAudioFile(relativeFilePath))
                    {
                        return false;
                    }

                    this._logger.LogInformation("已加载音频资源：{FileName}", relativeFilePath);
                }

                foreach (KeyValuePair<int, string> promptMedia in EmbeddedPromptMedia.SipAudioFiles)
                {
                    string relativeFilePath = NormalizeCacheKey(promptMedia.Value);
                    if (!this._audioFileCache.ContainsKey(relativeFilePath))
                    {
                        this._logger.LogWarning("SIP 状态码 {SipCode} 的音频资源 {FilePath} 不存在，无法缓存。", promptMedia.Key, promptMedia.Value);
                        return false;
                    }

                    this._sipAudioFilePaths[(SIPResponseStatusCodesEnum)promptMedia.Key] = relativeFilePath;
                }

                return true;
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "加载提示音缓存失败。");
                return false;
            }
        }

        public bool TryGetAudioBytes(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes)
        {
            if (this._sipAudioFilePaths.TryGetValue(sipCode, out string? relativeFilePath) && this._audioFileCache.TryGetValue(relativeFilePath, out audioBytes) && audioBytes is not null)
            {
                return true;
            }

            audioBytes = null;
            return false;
        }

        public bool TryGetAudioBytes(string relativeFilePath, out byte[]? audioBytes)
        {
            if (!string.IsNullOrWhiteSpace(relativeFilePath) && this._audioFileCache.TryGetValue(NormalizeCacheKey(relativeFilePath), out audioBytes) && audioBytes is not null)
            {
                return true;
            }

            audioBytes = null;
            return false;
        }

        public void Dispose()
        {
            this._audioFileCache.Clear();
            this._sipAudioFilePaths.Clear();
        }

        private bool CacheAudioFile(string relativeFilePath)
        {
            using Stream audioStream = EmbeddedPromptMedia.OpenRead(relativeFilePath);
            using IStreamAudioPlayer player = this._audioPlayerFactory();
            ArrayBufferWriter<byte> cachedAudioWriter = new();
            void OnAudioData(float[] audioData, bool _, bool __)
            {
                Span<byte> pcmBytes = cachedAudioWriter.GetSpan(audioData.Length * sizeof(short));
                for (int index = 0; index < audioData.Length; index++)
                {
                    float sample = Math.Clamp(audioData[index], -1.0f, 1.0f);
                    short pcm16 = sample >= 1.0f ? short.MaxValue : (short)(sample * 32768.0f);
                    BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.Slice(index * sizeof(short)), pcm16);
                }

                cachedAudioWriter.Advance(audioData.Length * sizeof(short));
            }

            player.OnAudioDataAvailable += OnAudioData;
            try
            {
                if (!player.CheckFFmpegInstalledAsync().GetAwaiter().GetResult() ||
                    !player.LoadAsync(
                            audioStream,
                            CachedSampleRate,
                            outputChannels: 1,
                            AudioProcessSettings.DefaultPacketTimeMs)
                        .GetAwaiter()
                        .GetResult())
                {
                    this._logger.LogWarning("音频资源 {FilePath} 无法解析。", relativeFilePath);
                    return false;
                }

                player.DecodeAsync().GetAwaiter().GetResult();
                byte[] cachedAudio = cachedAudioWriter.WrittenSpan.ToArray();
                if (cachedAudio.Length == 0)
                {
                    this._logger.LogWarning("音频资源 {FilePath} 未产生 PCM 数据。", relativeFilePath);
                    return false;
                }

                this._audioFileCache[NormalizeCacheKey(relativeFilePath)] = cachedAudio;
                return true;
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "缓存音频资源 {FilePath} 失败。", relativeFilePath);
                return false;
            }
            finally
            {
                player.OnAudioDataAvailable -= OnAudioData;
            }
        }

        private static string NormalizeCacheKey(string relativeFilePath)
        {
            return relativeFilePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
    }
}
