using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Helpers;
using Agent.Telephone.Resources;
using IAudioEditor = Agent.Telephone.Media.Abstractions.IAudioEditor;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Agent.Telephone.Providers.TTS.Sherpa
{
    internal class Kokoro : BaseSherpaTts<Kokoro>, ITts
    {
       

        public Kokoro(IAudioEditor audioEditor, ILogger<Kokoro> logger) : base(audioEditor, logger)
        {
        }

        public override string ModelName => nameof(Kokoro);

        public override bool Build(ModelSetting modelSetting)
        {
            try
            {
                if (!this.CheckModelExist())
                {
                    return false;
                }
                var config = new OfflineTtsConfig();
                config.Model.Kokoro.Model = Path.Combine(this.ModelFileFoler, "model.onnx");
                config.Model.Kokoro.Voices = Path.Combine(this.ModelFileFoler, "voices.bin");
                config.Model.Kokoro.Tokens = Path.Combine(this.ModelFileFoler, "tokens.txt");
                config.Model.Kokoro.DataDir = Path.Combine(this.ModelFileFoler, "espeak-ng-data");
                config.Model.Kokoro.DictDir = Path.Combine(this.ModelFileFoler, "dict");

                string? lexicons = modelSetting.Config.GetConfigValueOrDefault("Lexicons");
                if (!string.IsNullOrWhiteSpace(lexicons))
                {
                    string lexiconPath = string.Join(',', lexicons.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(l => Path.Combine(this.ModelFileFoler, l)));
                    config.Model.Kokoro.Lexicon = lexiconPath;
                }


                this.Build(config, modelSetting);

                this.Logger.LogInformation("已构建 {providerType} 模型：{modelName}", this.ProviderType, this.ModelName);
                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "{providerType} 配置无效：{modelName}", this.ProviderType, this.ModelName);
                return false;
            }
        }
    }
}
