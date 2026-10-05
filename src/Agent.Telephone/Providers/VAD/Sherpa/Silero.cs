using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Helpers;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Agent.Telephone.Providers.VAD.Sherpa
{
    internal class Silero : BaseSherpaVad<Silero>, IVad
    {
        public Silero(ILogger<Silero> logger) : base(logger)
        {
        }

        public override string ModelName => nameof(Silero);
        public override bool Build(ModelSetting modelSetting)
        {
            try
            {
                if (!this.CheckModelExist())
                {
                    return false;
                }
                VadModelConfig vadModelConfig = new VadModelConfig();
                vadModelConfig.SileroVad.Model = Path.Combine(this.ModelFileFoler, "model.onnx");
                vadModelConfig.SileroVad.Threshold = modelSetting.Config.GetConfigValueOrDefault("Threshold", 0.5f);
                vadModelConfig.SileroVad.MinSilenceDuration = modelSetting.Config.GetConfigValueOrDefault("SilenceThresholdSecond", 0.7f);
                vadModelConfig.SileroVad.MinSpeechDuration = modelSetting.Config.GetConfigValueOrDefault("MinSpeechDurationSecond", 0.5f);
                vadModelConfig.SileroVad.MaxSpeechDuration = modelSetting.Config.GetConfigValueOrDefault("MaxSpeechDurationSecond", 60.0f);

                if (this.Build(vadModelConfig, modelSetting))
                {
                    this.Logger.LogInformation("已构建 {providerType} 模型：{modelName}", this.ProviderType, this.ModelName);
                    return true;
                }
                else
                    return false;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "{providerType} 模型设置无效：{modelName}", this.ProviderType, this.ModelName);
                return false;
            }

        }

        
    }
}
