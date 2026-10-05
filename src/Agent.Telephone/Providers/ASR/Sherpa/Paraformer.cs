using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Media.Abstractions;

namespace Agent.Telephone.Providers.ASR.Sherpa
{
    internal class Paraformer : BaseSherpaAsr<Paraformer>, IAsr
    {
        public Paraformer(IAudioEditor audioEditor, ILogger<Paraformer> logger) : base(audioEditor, logger)
        {
        }

        public override string ModelName => nameof(Paraformer);

        public override bool Build(ModelSetting modelSetting)
        {
            try
            {
                if (!this.CheckModelExist())
                {
                    return false;
                }
                OfflineRecognizerConfig offlineRecognizerConfig = new OfflineRecognizerConfig();
                offlineRecognizerConfig.ModelConfig.Paraformer.Model = Path.Combine(this.ModelFileFoler, "model.onnx");

                this.Build(offlineRecognizerConfig, modelSetting);

                this.Logger.LogInformation("已构建 {ProviderType} 提供程序，模型: {ModelName}", this.ProviderType, this.ModelName);
                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "构建 {ProviderType} 提供程序时发生错误，模型: {ModelName}", this.ProviderType, this.ModelName);
                return false;
            }
        }
    }
}
