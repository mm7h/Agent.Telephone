using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Helpers;
using Agent.Telephone.Resources;
using IAudioEditor = Agent.Telephone.Media.Abstractions.IAudioEditor;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Agent.Telephone.Providers.ASR.Sherpa
{
    internal class SenseVoice : BaseSherpaAsr<SenseVoice>, IAsr
    {

        public SenseVoice(IAudioEditor audioEditor, ILogger<SenseVoice> logger) : base(audioEditor, logger)
        {
        }
        public override string ModelName => nameof(SenseVoice);
        public override string ProviderType => "asr";

        public override bool Build(ModelSetting modelSetting)
        {
            try
            {
                if (!this.CheckModelExist())
                {
                    return false;
                }
                OfflineRecognizerConfig offlineRecognizerConfig = new OfflineRecognizerConfig();
                offlineRecognizerConfig.ModelConfig.SenseVoice.Model = Path.Combine(this.ModelFileFoler, "model.onnx");
                offlineRecognizerConfig.ModelConfig.SenseVoice.UseInverseTextNormalization = modelSetting.Config.GetConfigValueOrDefault("UseInverseTextNormalization", 1);

                this.Build(offlineRecognizerConfig, modelSetting);

                this.Logger.LogInformation("已构建 {providerType} 模型: {modelName}", this.ProviderType, this.ModelName);

                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "{providerType} 模型设置无效: {modelName}", this.ProviderType, this.ModelName);
                return false;
            }
        }

    }
}
