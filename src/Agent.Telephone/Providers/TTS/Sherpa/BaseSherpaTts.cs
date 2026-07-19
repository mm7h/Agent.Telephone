using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using Agent.Telephone.Resources;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Agent.Telephone.Providers.TTS.Sherpa
{
    internal abstract class BaseSherpaTts<TLogger> : BaseProvider<TLogger, ModelSetting>, ITts
    {
        private readonly IAudioEditor _audioEditor;
        private readonly ConcurrentDictionary<string, ITtsEventCallback> _ttsSessions;
        private OfflineTts? _offlineTts;

        protected BaseSherpaTts(IAudioEditor audioEditor, ILogger<TLogger> logger) : base(logger)
        {
            this._audioEditor = audioEditor;
            this._ttsSessions = new ConcurrentDictionary<string, ITtsEventCallback>();
        }
        public override string ProviderType => "tts";
        public AudioSavingConfig? AudioSavingConfig { get; protected set; }
        //https://k2-fsa.github.io/sherpa/onnx/tts/pretrained_models/kokoro.html#map-between-speaker-id-and-speaker-name
        public int SpeakerId { get; private set; } = 50;
        public float SpeechRate { get; private set; } = 1.0f;

        public virtual int GetTtsSampleRate()
        {
            return this._offlineTts?.SampleRate ?? 24000;
        }

        protected void Build(OfflineTtsConfig offlineTtsConfig, ModelSetting modelSetting)
        {
            offlineTtsConfig.Model.NumThreads = 2;
            offlineTtsConfig.Model.Provider = "cpu";

            this.SpeechRate = modelSetting.Config.GetConfigValueOrDefault("SpeechRate", 1.0f);
            this.SpeakerId = modelSetting.Config.GetConfigValueOrDefault("SpeakerId", 50);
            this.AudioSavingConfig = modelSetting.Config.GetConfigValueOrDefault("FileSavingOption", new AudioSavingConfig(false));
            if (this.AudioSavingConfig.SaveFile && !Directory.Exists(this.AudioSavingConfig.SavePath))
            {
                Directory.CreateDirectory(this.AudioSavingConfig.SavePath);
            }
            this._offlineTts = new OfflineTts(offlineTtsConfig);
        }

        public void RegisterDevice(string deviceId, ITtsEventCallback callback)
        {
            this._ttsSessions.TryAdd(deviceId, callback);
        }

        public override void UnregisterDevice(string deviceId)
        {
            if (this._ttsSessions.TryRemove(deviceId, out _))
            {
                this.Logger.LogDebug("已注销设备 {deviceId} 的 TTS 会话", deviceId);
            }
        }

        public override bool CheckDeviceRegistered(string deviceId)
        {
            return this._ttsSessions.ContainsKey(deviceId);
        }

        public async Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(workflow.DeviceId))
            {
                throw new InvalidOperationException();
            }
            if (this._offlineTts == null)
            {
                throw new ArgumentNullException("请先构建 TTS 提供程序。");
            }

            try
            {
                OutSegment segment = workflow.Data;

                if (string.IsNullOrWhiteSpace(segment.ParagraphId) || string.IsNullOrWhiteSpace(segment.SentenceId))
                {
                    this.Logger.LogWarning("由于缺少段落 ID 或句子 ID，处理片段失败。");
                    return;
                }

                if (this._ttsSessions.TryGetValue(workflow.DeviceId, out ITtsEventCallback? sessionCallback) && sessionCallback is not null)
                {
                    Stopwatch timer = Stopwatch.StartNew();

                    bool firstFrameSent = false;

                    sessionCallback.OnBeforeProcessing(segment.Content, segment.IsFirstSegment, segment.IsLastSegment);

                    OfflineTtsGeneratedAudio audio = this._offlineTts.GenerateWithCallbackProgress(segment.Content, this.SpeechRate, this.SpeakerId, (nint samples, int n, float progress) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return 0;
                        }
                        float[] data = new float[n];
                        Marshal.Copy(samples, data, 0, n);

                        if (!firstFrameSent)
                        {
                            sessionCallback.OnSentenceStart(segment.Content, segment.SentenceId);
                            firstFrameSent = true;
                        }

                        if (progress == 1.0f)
                        {
                            sessionCallback.OnSentenceEnd(segment.Content, segment.SentenceId);
                        }

                        sessionCallback.OnProcessing(data, false, false);
                        return 1;
                    });

                    if (token.IsCancellationRequested)
                    {
                        sessionCallback.OnProcessed(segment.Content, segment.IsFirstSegment, segment.IsLastSegment, TtsGenerateResult.Aborted);
                    }
                    else
                    {
                        sessionCallback.OnProcessed(segment.Content, segment.IsFirstSegment, segment.IsLastSegment, TtsGenerateResult.Success);
                    }

                    double duration = Math.Max((this.CalculateDuration(audio.SampleRate, audio.NumSamples) * 1000 - (workflow.Data.IsFirstSegment ? 300 + timer.ElapsedMilliseconds : 0)), 0);


                    if (this.AudioSavingConfig is not null && this.AudioSavingConfig.SaveFile)
                    {
                        string fileName = $"{this.ProviderType}_{segment.SentenceId}.{this.AudioSavingConfig.Format}";
                        string filePath = Path.Combine(this.AudioSavingConfig.SavePath, fileName);
                        if (File.Exists(filePath))
                            File.Delete(filePath);
                        bool saved = await this._audioEditor.SaveAudioFileAsync(filePath, audio.Samples, this.GetTtsSampleRate(), 1, 128000);
                        if (saved)
                        {
                            this.Logger.LogDebug("保存 TTS wav 文件 {fileName} 成功，文件时长为：{duration}s。", fileName, this.FormatDuration(duration));
                        }
                        else
                        {
                            this.Logger.LogDebug("保存 TTS wav 文件 {fileName} 失败。", fileName);
                        }
                    }
                    else
                    {
                        this.Logger.LogDebug("生成 TTS 音频，文件时长为：{duration}s。", this.FormatDuration(duration));
                    }
                    audio.Dispose();
                    timer.Stop();
                }
                else
                {
                    this.Logger.LogError("设备 {deviceId} 未注册 TTS 事件回调。", workflow.DeviceId);
                }
                await Task.CompletedTask;

            }
            catch (OperationCanceledException)
            {
                this.Logger.LogWarning("用户取消了 {providerType} 任务。", this.ProviderType);
                throw;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "生成 {providerType} 音频时发生意外错误。", this.ProviderType);
            }
        }

        private double CalculateDuration(int sampleRate, int numSamples)
        {
            return (double)numSamples / sampleRate;
        }
        private string FormatDuration(double durationInMillisecond)
        {
            int durationInSeconds = (int)durationInMillisecond / 1000;
            int minutes = durationInSeconds / 60;
            int seconds = durationInSeconds % 60;
            return $"{minutes}m {seconds}s";
        }
        public override void Dispose()
        {
            this._offlineTts?.Dispose();
        }
    }
}
