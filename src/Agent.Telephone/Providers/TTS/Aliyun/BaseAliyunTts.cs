using System.Collections.Concurrent;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.TTS.Aliyun
{
    internal abstract class BaseAliyunTts<TLogger> : BaseProvider<TLogger, ModelSetting>
    {
        private readonly IAudioEditor _audioEditor;
        private readonly ConcurrentDictionary<string, string> _savedAudioPaths = new();

        protected BaseAliyunTts(IAudioEditor audioEditor, ILogger<TLogger> logger)
            : base(logger)
        {
            this._audioEditor = audioEditor;
        }

        public override string ProviderType => "tts";

        public string? GetSavedAudioFilePath(string sentenceId) =>
            this._savedAudioPaths.TryGetValue(sentenceId, out string? path) ? path : null;

        protected void BuildAudioSavingConfig(ModelSetting modelSetting)
        {
            this.AudioSavingConfig = modelSetting.Config.GetConfigValueOrDefault(
                "FileSavingOption",
                new AudioSavingConfig(false));
            if (this.AudioSavingConfig.SaveFile && !Directory.Exists(this.AudioSavingConfig.SavePath))
            {
                Directory.CreateDirectory(this.AudioSavingConfig.SavePath);
            }
        }

        protected async Task SaveAudioFileAsync(string sentenceId, float[] audioData)
        {
            if (this.AudioSavingConfig is not { SaveFile: true } ||
                string.IsNullOrWhiteSpace(sentenceId) ||
                audioData.Length == 0)
            {
                return;
            }

            string fileName = FileNameHelper.CreateAudioFileName(
                this.ProviderType,
                this.CurrentCall.CallerNumber,
                this.CurrentCall.DialedNumber,
                FileNameHelper.GetIndex(sentenceId, this.CurrentCall.DeviceId),
                this.AudioSavingConfig.Format);
            string filePath = Path.Combine(this.AudioSavingConfig.SavePath, fileName);
            try
            {
                int outputSampleRate = this.GetNegotiatedAudioSavingSampleRate();
                float[] savedAudio = ResampleForAudioSaving(
                    audioData,
                    AudioProcessSettings.ModelToInputSampleRate,
                    outputSampleRate);
                bool saved = await this._audioEditor.SaveAudioFileAsync(
                    filePath,
                    savedAudio,
                    outputSampleRate,
                    channels: 1,
                    bitRate: this.AudioSavingConfig.BitRate);
                if (saved)
                {
                    this._savedAudioPaths[sentenceId] = filePath;
                    this.Logger.LogInformation("已保存阿里云 TTS 音频 {FileName}。", fileName);
                }
                else
                {
                    this.Logger.LogWarning("保存阿里云 TTS 音频 {FileName} 失败。", fileName);
                }
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "保存阿里云 TTS 音频 {FileName} 失败。", fileName);
            }
        }

        protected AudioSavingConfig? AudioSavingConfig { get; private set; }
    }
}
