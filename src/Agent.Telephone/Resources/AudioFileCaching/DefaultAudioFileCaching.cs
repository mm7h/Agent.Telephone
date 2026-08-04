using System.Collections.Concurrent;
using Agent.Telephone.Common.Constants;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using ISIPSorceryAudioCodec = SIPSorcery.Media.AudioEncoder;

namespace Agent.Telephone.Resources.AudioFileCaching
{
    internal class DefaultAudioFileCaching : BaseResource<DefaultAudioFileCaching, IDictionary<int, string>>, IAudioFileCaching
    {
        private const string END_OF_FILE = "End of file";
        private static readonly TimeSpan s_fileDecodeTimeout = TimeSpan.FromSeconds(180);
        private readonly ISIPSorceryAudioCodec _audioCodec;
        private readonly IDictionary<SIPResponseStatusCodesEnum, byte[]> _audioFileCache = new ConcurrentDictionary<SIPResponseStatusCodesEnum, byte[]>();
        private AudioFormat _supportedAudioFormat = AudioFormat.Empty;

        public DefaultAudioFileCaching(ISIPSorceryAudioCodec audioCodec, ILogger<DefaultAudioFileCaching> logger) : base(logger)
        {
            this._audioCodec = audioCodec;
        }

        public override string ResourceName => nameof(DefaultAudioFileCaching);


        public override bool Load(IDictionary<int, string> settings)
        {
            try
            {
                foreach (KeyValuePair<int, string> kvp in settings)
                {
                    // 空路径表示该提示音未配置，属于可选提示音，跳过即可。
                    if (string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        continue;
                    }

                    if (this._supportedAudioFormat.IsEmpty())
                    {
                        this._supportedAudioFormat = SupportedAudioFormats
                            .SupportedSDPAudioFormat
                            .First(format => format.Codec == AudioCodecsEnum.PCMU);
                    }

                    if (!this.CacheAudioFile((SIPResponseStatusCodesEnum)kvp.Key, kvp.Value))
                    {
                        this.Logger.LogWarning("音频文件 {FilePath} 缓存失败。", kvp.Value);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "加载音频文件缓存失败。");
                return false;
            }
        }

        public bool TryGetAudioBytes(SIPResponseStatusCodesEnum sipCode, out byte[]? audioBytes)
        {
            if (this._audioFileCache.TryGetValue(sipCode, out audioBytes) && audioBytes is not null)
            {
                return true;
            }

            this.Logger.LogWarning("SIP code {SipCode} 未缓存对应的音频文件数据。", sipCode);
            audioBytes = null;
            return false;
        }

        private bool CacheAudioFile(SIPResponseStatusCodesEnum cacheKey, string filePath)
        {
            if (!File.Exists(filePath))
            {
                this.Logger.LogWarning("音频文件 {FilePath} 不存在，无法缓存。", filePath);
                return false;
            }

            FFmpegFileSource source = new FFmpegFileSource(filePath, false, this._audioCodec, 160, false);
            ConcurrentQueue<byte[]> audioSegments = new ConcurrentQueue<byte[]>();
            TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnRawSample(AudioSamplingRatesEnum samplingRate, uint duration, short[] sample)
            {
                short[] resampled = PcmResampler.Resample(sample, (int)samplingRate, this._supportedAudioFormat.ClockRate);
                byte[] pcmBytes = new byte[resampled.Length * sizeof(short)];
                Buffer.BlockCopy(resampled, 0, pcmBytes, 0, pcmBytes.Length);
                audioSegments.Enqueue(pcmBytes);
            }

            void OnSourceError(string error)
            {
                if (string.Equals(error, END_OF_FILE, StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException($"FFmpeg audio source failed: {error}"));
                }
            }

            source.OnAudioSourceRawSample += OnRawSample;
            source.OnAudioSourceError += OnSourceError;
            try
            {
                source.RestrictFormats(format => format.Codec == this._supportedAudioFormat.Codec);
                source.SetAudioSourceFormat(this._supportedAudioFormat);
                Task startAudio = source.StartAudio();
                Task.WhenAll(startAudio, completion.Task)
                    .WaitAsync(s_fileDecodeTimeout)
                    .GetAwaiter()
                    .GetResult();

                int totalLength = audioSegments.Sum(segment => segment.Length);
                byte[] cachedAudio = new byte[totalLength];
                int offset = 0;
                foreach (byte[] segment in audioSegments)
                {
                    Buffer.BlockCopy(segment, 0, cachedAudio, offset, segment.Length);
                    offset += segment.Length;
                }

                this._audioFileCache[cacheKey] = cachedAudio;
                return true;
            }
            catch (TimeoutException)
            {
                this.Logger.LogWarning("缓存音频文件 {FilePath} 超时，最大解析时长为 {TimeoutSeconds} 秒。", filePath, s_fileDecodeTimeout.TotalSeconds);
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "缓存音频文件 {FilePath} 失败。", filePath);
                return false;
            }
            finally
            {
                source.OnAudioSourceRawSample -= OnRawSample;
                source.OnAudioSourceError -= OnSourceError;
                try
                {
                    source.CloseAudio().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    this.Logger.LogDebug(exception, "关闭 FFmpeg 音频源 {FilePath} 时发生异常。", filePath);
                }

                source.Dispose();
            }
        }

        public override void Dispose()
        {
            this._audioFileCache.Clear();
        }
    }
}
