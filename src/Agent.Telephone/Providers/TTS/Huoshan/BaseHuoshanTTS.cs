using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Helpers;
using Agent.Telephone.Resources;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.TTS.Huoshan
{
    internal abstract class BaseHuoshanTTS<TLogger> : BaseProvider<TLogger, ModelSetting>
    {
        private const string LANG_ZH = "zh-CN";
        private const int SAMPLE_RATE = 24000;

        private readonly IAudioEditor _audioEditor;

        public BaseHuoshanTTS(IAudioEditor audioEditor, ILogger<TLogger> logger) : base(logger)
        {
            this._audioEditor = audioEditor;
        }

        public override string ProviderType => "tts";
        public string SpeakerId { get; protected set; } = string.Empty;
        public int SpeechRate { get; protected set; } = 0;
        public int LoudnessRate { get; protected set; } = 0;
        protected string AudioEncoding { get; set; } = "pcm";
        public AudioSavingConfig? AudioSavingConfig { get; protected set; }
        protected ITtsEventCallback? TTSEventCallback { get; set; }
        public int GetTtsSampleRate() => SAMPLE_RATE;

        public void RegisterDevice(string deviceId, ITtsEventCallback callback)
        {
            this.TTSEventCallback = callback;
            this.RegisterDevice(deviceId);
        }

        protected void BuildAudioSavingConfig(ModelSetting modelSetting)
        {
            this.AudioSavingConfig = modelSetting.Config.GetConfigValueOrDefault("FileSavingOption", new AudioSavingConfig(false));
            if (this.AudioSavingConfig.SaveFile && !Directory.Exists(this.AudioSavingConfig.SavePath))
            {
                Directory.CreateDirectory(this.AudioSavingConfig.SavePath);
            }
        }

        protected async Task<bool> SaveAudioFileAsync(string deviceId, string fileName, float[] audioData)
        {
            if (this.AudioSavingConfig is not null && this.AudioSavingConfig.SaveFile)
            {
                fileName = $"{this.ProviderType}_{fileName}.{this.AudioSavingConfig.Format}";
                string savingPath = Path.Combine(this.AudioSavingConfig.SavePath, fileName);
                try
                {
                    bool saved = await this._audioEditor.SaveAudioFileAsync(savingPath, audioData, this.GetTtsSampleRate(), 1, 128000);

                    if (saved)
                    {
                        this.Logger.LogInformation("已将 TTS 音频文件保存到 {audioPath}，设备 {deviceId}。", fileName, deviceId);
                    }
                    else
                    {
                        this.Logger.LogWarning("保存音频文件 {fileName} 失败，设备 Id {deviceId}。", fileName, deviceId);
                    }
                    return saved;
                }
                catch (Exception ex)
                {
                    this.Logger.LogError(ex, "保存音频文件 {fileName} 失败，设备 Id {deviceId}。", fileName, deviceId);
                    return false;
                }

            }
            else
            {
                return true;
            }
        }
    }
}
