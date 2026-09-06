using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.VAD.Contexts;
using Agent.Telephone.Resources.OnnxModels;
using Agent.Telephone.Resources.OnnxModels.VAD;
using Agent.Telephone.Resources.OnnxModels.VAD.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.VAD.Native
{
    /// <summary>
    /// Silero VAD v4 implementation
    /// </summary>
    internal sealed class SileroNative : BaseProvider<SileroNative, ModelSetting>, IVad
    {
        private readonly IServiceProvider _serviceProvider;

        private IVadOnnxModel? _vadOnnxModel;
        private int _closeConnectionNoVoiceTime = 120_000;

        private float _silenceThresholdSecond;
        private float _threshold;
        private float _thresholdLow;

        private const int FRAME_WINDOW_THRESHOLD = 5;

        private SileroModelState? _sileroModelState;
        private VadSessionState? _vadSessionState;
        private float[] _pendingAudio = [];

        private IVadEventCallback? _vadEventCallback;

        public SileroNative(IServiceProvider serviceProvider, ILogger<SileroNative> logger) : base(logger)
        {
            this._serviceProvider = serviceProvider;
        }

        public override string ProviderType => "vad";
        public override string ModelName => nameof(SileroNative);

        public int FrameSize { get; private set; }

        public override bool Build(ModelSetting modelSetting)
        {
            try
            {
                this._silenceThresholdSecond = modelSetting.Config.GetConfigValueOrDefault("SilenceThresholdSecond", 0.7f);
                this._threshold = modelSetting.Config.GetConfigValueOrDefault("Threshold", 0.5f);
                this._thresholdLow = modelSetting.Config.GetConfigValueOrDefault("ThresholdLow", 0.2f);
                this._closeConnectionNoVoiceTime = modelSetting.Config.GetConfigValueOrDefault("CloseConnectionNoVoiceTime", 120_000);

                this.FrameSize = 512;

                this._vadOnnxModel = this._serviceProvider.GetRequiredService<IVadOnnxModel>();

                this._sileroModelState = SileroOnnx.CreateModelState(AudioProcessSettings.OutputToModelSampleRate);

                this.Logger.LogInformation("已构建 {providerType} 模型：{modelName}", this.ProviderType, this.ModelName);

                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "{providerType} 配置无效：{modelName}", this.ProviderType, this.ModelName);
                return false;
            }
        }
        public void RegisterDevice(ActiveCallContext activeCall, IVadEventCallback callback)
        {
            this._vadEventCallback = callback;
            this._vadSessionState = new VadSessionState();

            this.RegisterDevice(activeCall);
        }

        public void ResetSessionState(string deviceId)
        {
            this._sileroModelState?.Reset();
            this._vadSessionState?.Reset();
            this._pendingAudio = [];
        }

        public Task AnalysisVoiceAsync(
            string deviceId,
            float[] newAudioData,
            float[] bufferedAudioData,
            CancellationToken token)
        {
            if (this._vadOnnxModel is null)
            {
                throw new ArgumentNullException("请先构建 VAD 提供程序。");
            }

            if (this._sileroModelState is null || this._vadSessionState is null)
            {
                throw new ArgumentNullException("请先构建 VAD 提供程序。");
            }

            try
            {
                float[] pendingAndNewAudio = this.CombinePendingAudio(newAudioData);
                int analyzedIndex = 0;
                bool analyzedFrame = false;

                while (pendingAndNewAudio.GetSlidingFrame(this.FrameSize, ref analyzedIndex, out float[] chunk))
                {
                    token.ThrowIfCancellationRequested();

                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    analyzedFrame = true;

                    float speechProb = this._vadOnnxModel.Infer(chunk, AudioProcessSettings.OutputToModelSampleRate, this._sileroModelState);

                    bool isSpeechDetected;
                    if (speechProb >= this._threshold)
                    {
                        isSpeechDetected = true;
                    }
                    else if (speechProb <= this._thresholdLow)
                    {
                        isSpeechDetected = false;
                    }
                    else
                    {
                        isSpeechDetected = this._vadSessionState.LastIsVoice;
                    }

                    this._vadSessionState.LastIsVoice = isSpeechDetected;
                    if (isSpeechDetected)
                    {
                        this._vadSessionState.AppendSpeechAudio(chunk);
                    }

                    if (!this._vadSessionState.HaveVoice)
                    {
                        this._vadSessionState.AddToVoiceWindow(isSpeechDetected);
                        if (this._vadSessionState.CountVoiceInWindow() >= FRAME_WINDOW_THRESHOLD)
                        {
                            this._vadSessionState.HaveVoice = true;
                            this._vadSessionState.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                            this._vadSessionState.SilenceFrameCount = 0;
                        }
                    }
                    else if (isSpeechDetected)
                    {
                        this._vadSessionState.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        this._vadSessionState.SilenceFrameCount = 0;
                    }
                    else
                    {
                        this._vadSessionState.SilenceFrameCount++;
                        int silenceDurationMs = this._vadSessionState.SilenceFrameCount * this.FrameSize * 1000 / AudioProcessSettings.OutputToModelSampleRate;
                        if (silenceDurationMs >= this._silenceThresholdSecond * 1000)
                        {
                            this.Logger.LogDebug("设备 {deviceId} 的语音已停止，静默持续时间：{silenceDuration}ms", deviceId, silenceDurationMs);
                            this._vadSessionState.VoiceStop = true;

                            this._vadEventCallback?.OnVoiceDetected(this._vadSessionState.TakeSpeechAudio());
                            this._vadSessionState.Reset();
                            this._pendingAudio = [];
                            return Task.CompletedTask;
                        }
                    }

                    if (!this._vadSessionState.HaveVoice && this._vadSessionState.CountVoiceInWindow() == 0)
                    {
                        this._vadSessionState.ResetSpeechAudio();
                    }
                }

                this._pendingAudio = pendingAndNewAudio[analyzedIndex..];

                if (!this._vadSessionState.HaveVoice && analyzedFrame)
                {
                    this._vadEventCallback?.OnVoiceSilence();
                }

                this.CheckLongTermSilence(deviceId, this._vadSessionState);

                return Task.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                this._sileroModelState.Reset();
                this._vadSessionState.Reset();
                this._pendingAudio = [];
                this.Logger.LogWarning("用户取消了 {providerType} 任务。", this.ProviderType);
                throw;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "设备 {deviceId} 的 {providerType} 发生意外错误", this.ProviderType, deviceId);
                return Task.CompletedTask;
            }
        }

        private float[] CombinePendingAudio(float[] audioData)
        {
            if (this._pendingAudio.Length == 0)
            {
                return audioData;
            }

            float[] combined = new float[this._pendingAudio.Length + audioData.Length];
            Array.Copy(this._pendingAudio, combined, this._pendingAudio.Length);
            Array.Copy(audioData, 0, combined, this._pendingAudio.Length, audioData.Length);
            return combined;
        }

        private void CheckLongTermSilence(string deviceId, VadSessionState vadState)
        {
            if (vadState.HaveVoiceLatestTime == 0)
            {
                vadState.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                return;
            }

            long silenceDuration = DateTimeOffset.Now.ToUnixTimeMilliseconds() - vadState.HaveVoiceLatestTime;
            long longTermSilenceThresholdMs = this._closeConnectionNoVoiceTime;

            if (silenceDuration >= longTermSilenceThresholdMs)
            {
                this.Logger.LogDebug("检测到设备 {deviceId} 的长期静默，持续时间：{silenceDuration}ms", deviceId, silenceDuration);
                this._vadEventCallback?.OnLongTermSilence();
            }
        }

        public override void Dispose()
        {
            this._sileroModelState?.Reset();
            this._vadSessionState?.Reset();
            this._pendingAudio = [];
        }
    }
}
