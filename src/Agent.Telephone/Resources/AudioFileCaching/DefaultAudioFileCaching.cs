using System.Collections.Concurrent;
using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Resources.AudioFileCaching
{
    internal sealed class DefaultAudioFileCaching : BaseResource<DefaultAudioFileCaching, AudioFileCachingBuildConfig>, IAudioFileCaching
    {
        private const int CachedSampleRate = 8000;
        private readonly IDictionary<SIPResponseStatusCodesEnum, byte[]> _audioFileCache =
            new ConcurrentDictionary<SIPResponseStatusCodesEnum, byte[]>();
        private readonly Func<IUrlAudioPlayer> _audioPlayerFactory;

        public DefaultAudioFileCaching(
            Func<IUrlAudioPlayer> audioPlayerFactory,
            ILogger<DefaultAudioFileCaching> logger)
            : base(logger)
        {
            this._audioPlayerFactory = audioPlayerFactory;
        }

        public override string ResourceName => nameof(DefaultAudioFileCaching);

        public override bool Load(AudioFileCachingBuildConfig settings)
        {
            try
            {
                foreach (KeyValuePair<int, string> promptMedia in settings.PromptMediaConfigs)
                {
                    if (string.IsNullOrWhiteSpace(promptMedia.Value))
                    {
                        continue;
                    }

                    if (!this.CacheAudioFile(settings.PromptMediaPath, (SIPResponseStatusCodesEnum)promptMedia.Key, promptMedia.Value))
                    {
                        return false;
                    }
                    else
                    {
                        this.Logger.LogInformation("已加载音频文件：{fileName}", Path.GetFileName(promptMedia.Value));
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "加载提示音缓存失败。");
                return false;
            }
        }

        public bool TryGetAudioBytes(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes)
        {
            if (this._audioFileCache.TryGetValue(sipCode, out audioBytes) && audioBytes is not null)
            {
                return true;
            }

            audioBytes = null;
            return false;
        }

        public override void Dispose()
        {
            this._audioFileCache.Clear();
        }

        private bool CacheAudioFile(string promptMediaPath, SIPResponseStatusCodesEnum cacheKey, string fileName)
        {
            string filePath = Path.Combine(promptMediaPath, fileName);
            if (!File.Exists(filePath))
            {
                this.Logger.LogWarning("音频文件 {FilePath} 不存在，无法缓存。", filePath);
                return false;
            }

            using IUrlAudioPlayer player = this._audioPlayerFactory();
            ConcurrentQueue<byte[]> frames = new();
            void OnAudioData(float[] audioData, bool _, bool __)
            {
                short[] pcm16 = audioData.PcmFloatToShort();
                byte[] bytes = new byte[pcm16.Length * sizeof(short)];
                Buffer.BlockCopy(pcm16, 0, bytes, 0, bytes.Length);
                frames.Enqueue(bytes);
            }

            player.OnAudioDataAvailable += OnAudioData;
            try
            {
                if (!player.CheckFFmpegInstalledAsync().GetAwaiter().GetResult() ||
                    !player.LoadAsync(
                            filePath,
                            CachedSampleRate,
                            outputChannels: 1,
                            AudioProcessSettings.DefaultPacketTimeMs)
                        .GetAwaiter()
                        .GetResult())
                {
                    this.Logger.LogWarning("音频文件 {FilePath} 无法解析。", filePath);
                    return false;
                }

                player.PlayAsync().GetAwaiter().GetResult();
                byte[] cachedAudio = frames.SelectMany(static frame => frame).ToArray();
                if (cachedAudio.Length == 0)
                {
                    this.Logger.LogWarning("音频文件 {FilePath} 未产生 PCM 数据。", filePath);
                    return false;
                }

                this._audioFileCache[cacheKey] = cachedAudio;
                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "缓存音频文件 {FilePath} 失败。", filePath);
                return false;
            }
            finally
            {
                player.OnAudioDataAvailable -= OnAudioData;
            }
        }
    }
}
