using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.VAD.Contexts;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using System.Collections.Concurrent;

namespace Agent.Telephone.Providers.VAD.Sherpa
{
    internal abstract class BaseSherpaVad<TLogger> : BaseProvider<TLogger, ModelSetting>
    {
        private VoiceActivityDetector? _vad;
        private int _sampleRate = 16000;
        private int _closeConnectionNoVoiceTime = 120;

        private const int SAMPLING_RATE_8K = 8000;
        private const int SAMPLING_RATE_16K = 16000;

        private readonly SemaphoreSlim _vadConvertSlim;
        private readonly ConcurrentDictionary<string, (VadSessionState, IVadEventCallback)> _vadSessions;

        protected BaseSherpaVad(ILogger<TLogger> logger) : base(logger)
        {
            this._vadConvertSlim = new SemaphoreSlim(1, 1);
            this._vadSessions = new ConcurrentDictionary<string, (VadSessionState, IVadEventCallback)>();
        }

        public override string ProviderType => "vad";
        public int FrameSize { get; private set; }

        public bool Build(VadModelConfig vadModelConfig, ModelSetting modelSetting)
        {
            this._sampleRate = modelSetting.Config.GetConfigValueOrDefault("SampleRate", 16000);

            if (this._sampleRate != SAMPLING_RATE_8K && this._sampleRate != SAMPLING_RATE_16K)
            {
                this.Logger.LogError("不支持的采样率：{sampleRate}。仅支持 8000 和 16000。", this._sampleRate);
                return false;
            }

            this._closeConnectionNoVoiceTime = modelSetting.Config.GetConfigValueOrDefault("CloseConnectionNoVoiceTime", 120_000);

            vadModelConfig.SampleRate = this._sampleRate;
            this.FrameSize = this._sampleRate == SAMPLING_RATE_16K ? 512 : 256;
            this._vad = new VoiceActivityDetector(vadModelConfig, 60);

            return true;
        }

        public void RegisterDevice(string deviceId, IVadEventCallback callback)
        {
            VadSessionState vadState = new VadSessionState();
            this._vadSessions.AddOrUpdate(deviceId, (vadState, callback), (_, _) => (vadState, callback));
            this.Logger.LogDebug("已注册设备的 VAD 会话状态：{deviceId}", deviceId);
        }

        public override void UnregisterDevice(string deviceId)
        {
            if (this._vadSessions.TryRemove(deviceId, out _))
            {
                this.Logger.LogDebug("已注销设备的 VAD 会话状态：{deviceId}", deviceId);
            }
        }

        public void ResetSessionState(string deviceId)
        {
            if (this._vadSessions.TryGetValue(deviceId, out var context))
            {
                var (state, _) = context;
                state.Reset();
                this.Logger.LogDebug("已重置设备的 VAD 会话状态：{deviceId}", deviceId);
            }
        }

        public override bool CheckDeviceRegistered(string deviceId)
        {
            return this._vadSessions.ContainsKey(deviceId);
        }

        public async Task AnalysisVoiceAsync(string deviceId,  float[] audioData, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(deviceId))
            {
                throw new InvalidOperationException();
            }
            if (this._vad is null)
            {
                throw new ArgumentNullException("请先构建 VAD 提供程序。");
            }

            if (!this._vadSessions.TryGetValue(deviceId, out var context))
            {
                throw new InvalidOperationException(string.Format("未找到设备 {0} 的会话状态。请先注册设备。", deviceId));
            }
            var (vadState, callback) = context;
            try
            {
                await this._vadConvertSlim.WaitAsync(token);

                this._vad.Reset();

                int analyzedIndex = vadState.AnalyzedIndex;

                while (audioData.GetSlidingFrame(this.FrameSize, ref analyzedIndex, out float[] chunk))
                {
                    token.ThrowIfCancellationRequested();

                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    this._vad.AcceptWaveform(chunk);

                    bool isSpeaking = this._vad.IsSpeechDetected();

                    if (isSpeaking)
                    {
                        vadState.HaveVoice = true;
                        vadState.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        continue;
                    }
                    else
                    {
                        if (!this._vad.IsEmpty())
                        {
                            SpeechSegment speechSegment = this._vad.Front();
                            this.Logger.LogDebug("设备语音已停止：{deviceId}。", deviceId);

                            callback.OnVoiceDetected(speechSegment.Samples);
                            vadState.Reset();

                            return;
                        }
                    }
                }

                if (!this._vad.IsSpeechDetected() && analyzedIndex > this.FrameSize * 50)
                {
                    callback.OnVoiceSilence();
                }

                this.CheckLongTermSilence(deviceId, vadState);
            }
            catch (OperationCanceledException)
            {
                vadState.Reset();
                this.Logger.LogWarning("用户取消了语音分析操作：{providerType}", this.ProviderType);
                throw;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "语音分析过程中发生意外错误：{providerType}", this.ProviderType);
            }
            finally
            {
                vadState.AnalyzedIndex = 0;
                this._vad.Reset();
                this._vadConvertSlim.Release();
            }
        }

        private void CheckLongTermSilence(string deviceId, VadSessionState vadState)
        {
            if (this._vadSessions.TryGetValue(deviceId, out var context))
            {
                var (_, callback) = context;
                if (vadState.HaveVoiceLatestTime == 0)
                {
                    vadState.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    return;
                }

                long silenceDuration = DateTimeOffset.Now.ToUnixTimeMilliseconds() - vadState.HaveVoiceLatestTime;

                if (silenceDuration >= this._closeConnectionNoVoiceTime)
                {
                    this.Logger.LogDebug("检测到设备长期静默：{deviceId}，持续时间：{silenceDuration}ms", deviceId, silenceDuration);
                    callback.OnLongTermSilence();
                }
            }
        }

        public override void Dispose()
        {
            this._vadSessions.Clear();
            this._vadConvertSlim.Dispose();
            this._vad?.Clear();
            this._vad?.Dispose();
        }
    }
}
