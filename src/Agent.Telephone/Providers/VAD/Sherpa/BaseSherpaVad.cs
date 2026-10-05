using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.VAD.Contexts;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using System.Collections.Concurrent;

namespace Agent.Telephone.Providers.VAD.Sherpa
{
    internal abstract class BaseSherpaVad<TLogger> : BaseProvider<TLogger, ModelSetting>
    {
        private const int SampleRate = AudioProcessSettings.OutputToModelSampleRate;
        private const int FrameSizeInSamples = 512;

        private int _closeConnectionNoVoiceTime = 120_000;
        private VadModelConfig? _vadModelConfig;

        private readonly ConcurrentDictionary<string, SherpaVadSession> _vadSessions;

        protected BaseSherpaVad(ILogger<TLogger> logger) : base(logger)
        {
            this._vadSessions = new ConcurrentDictionary<string, SherpaVadSession>();
        }

        public override string ProviderType => "vad";
        public int FrameSize => FrameSizeInSamples;

        public bool Build(VadModelConfig vadModelConfig, ModelSetting modelSetting)
        {
            this._closeConnectionNoVoiceTime = modelSetting.Config.GetConfigValueOrDefault("CloseConnectionNoVoiceTime", 120_000);
            vadModelConfig.SampleRate = SampleRate;
            this._vadModelConfig = vadModelConfig;
            return true;
        }

        public void RegisterDevice(ActiveCallContext activeCall, IVadEventCallback callback)
        {
            if (this._vadModelConfig is null)
            {
                throw new InvalidOperationException("请先构建 VAD 提供程序。");
            }

            VadModelConfig vadModelConfig = this._vadModelConfig.Value;
            var session = new SherpaVadSession(
                new VoiceActivityDetector(vadModelConfig, 60),
                new VadSessionState(),
                callback);
            this._vadSessions.AddOrUpdate(
                activeCall.DeviceId,
                session,
                (_, existingSession) =>
                {
                    existingSession.Dispose();
                    return session;
                });
            base.RegisterDevice(activeCall);
            this.Logger.LogDebug("已注册设备的 VAD 会话状态：{deviceId}", activeCall.DeviceId);
        }

        public override void UnregisterDevice(ActiveCallContext activeCall)
        {
            if (this._vadSessions.TryRemove(activeCall.DeviceId, out SherpaVadSession? session))
            {
                session.Dispose();
                this.Logger.LogDebug("已注销设备的 VAD 会话状态：{deviceId}", activeCall.DeviceId);
            }
            base.UnregisterDevice(activeCall);
        }

        public void ResetSessionState(string deviceId)
        {
            if (this._vadSessions.TryGetValue(deviceId, out SherpaVadSession? session))
            {
                session.Reset();
                this.Logger.LogDebug("已重置设备的 VAD 会话状态：{deviceId}", deviceId);
            }
        }

        public override bool CheckDeviceRegistered(string deviceId)
        {
            return this._vadSessions.ContainsKey(deviceId);
        }

        public Task AnalysisVoiceAsync(
            string deviceId,
            float[] newAudioData,
            float[] bufferedAudioData,
            CancellationToken token)
        {
            if (!this._vadSessions.TryGetValue(deviceId, out SherpaVadSession? session))
            {
                throw new InvalidOperationException(string.Format("未找到设备 {0} 的会话状态。请先注册设备。", deviceId));
            }

            try
            {
                float[] pendingAndNewAudio = session.CombinePendingAudio(newAudioData);
                int analyzedIndex = 0;
                bool analyzedFrame = false;

                while (pendingAndNewAudio.GetSlidingFrame(this.FrameSize, ref analyzedIndex, out float[] chunk))
                {
                    token.ThrowIfCancellationRequested();
                    analyzedFrame = true;
                    session.Detector.AcceptWaveform(chunk);

                    if (session.Detector.IsSpeechDetected())
                    {
                        bool voiceStarted = !session.State.HaveVoice;
                        session.State.HaveVoice = true;
                        session.State.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        if (voiceStarted)
                        {
                            session.Callback.OnVoiceStarted();
                        }
                        continue;
                    }

                    if (!session.Detector.IsEmpty())
                    {
                        SpeechSegment speechSegment = session.Detector.Front();
                        this.Logger.LogDebug("设备语音已停止：{deviceId}。", deviceId);

                        session.Callback.OnVoiceDetected(speechSegment.Samples);
                        session.Reset();
                        return Task.CompletedTask;
                    }
                }

                session.PendingAudio = pendingAndNewAudio[analyzedIndex..];

                if (!session.State.HaveVoice && analyzedFrame)
                {
                    session.Callback.OnVoiceSilence();
                }

                this.CheckLongTermSilence(deviceId, session);
                return Task.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                session.Reset();
                this.Logger.LogWarning("用户取消了语音分析操作：{providerType}", this.ProviderType);
                throw;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "语音分析过程中发生意外错误：{providerType}", this.ProviderType);
                return Task.CompletedTask;
            }
        }

        private void CheckLongTermSilence(string deviceId, SherpaVadSession session)
        {
            // 每个设备使用自己的回调状态，共享 VAD 不能读取其他通话的等待状态。
            if (session.State.HaveVoiceLatestTime == 0 || session.Callback.IsWaitingForReply)
            {
                session.State.HaveVoiceLatestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                return;
            }

            long silenceDuration = DateTimeOffset.Now.ToUnixTimeMilliseconds() - session.State.HaveVoiceLatestTime;
            if (silenceDuration >= this._closeConnectionNoVoiceTime)
            {
                this.Logger.LogDebug("检测到设备长期静默：{deviceId}，持续时间：{silenceDuration}ms", deviceId, silenceDuration);
                session.Callback.OnLongTermSilence();
            }
        }

        public override void Dispose()
        {
            foreach (SherpaVadSession session in this._vadSessions.Values)
            {
                session.Dispose();
            }

            this._vadSessions.Clear();
        }

        private sealed class SherpaVadSession : IDisposable
        {
            public SherpaVadSession(
                VoiceActivityDetector detector,
                VadSessionState state,
                IVadEventCallback callback)
            {
                this.Detector = detector;
                this.State = state;
                this.Callback = callback;
                this.PendingAudio = [];
            }

            public VoiceActivityDetector Detector { get; }
            public VadSessionState State { get; }
            public IVadEventCallback Callback { get; }
            public float[] PendingAudio { get; set; }

            public float[] CombinePendingAudio(float[] audioData)
            {
                if (this.PendingAudio.Length == 0)
                {
                    return audioData;
                }

                float[] combined = new float[this.PendingAudio.Length + audioData.Length];
                Array.Copy(this.PendingAudio, combined, this.PendingAudio.Length);
                Array.Copy(audioData, 0, combined, this.PendingAudio.Length, audioData.Length);
                return combined;
            }

            public void Reset()
            {
                this.Detector.Reset();
                this.State.Reset();
                this.PendingAudio = [];
            }

            public void Dispose()
            {
                this.Detector.Clear();
                this.Detector.Dispose();
            }
        }
    }
}
