using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Media;
using Agent.Telephone.Resources;
using Agent.Telephone.Resources.AudioFileCaching;
using Agent.Telephone.Resources.OnnxModels;
using Agent.Telephone.Resources.OnnxModels.VAD;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;

namespace Agent.Telephone.Management
{
    internal sealed class ResourceManager : BaseManager
    {
        public ResourceManager(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            ILogger<ResourceManager> logger)
            : base(serviceProvider, config, logger)
        {
        }

        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            return builder.ConfigureServices((_, services) =>
            {
                services.AddSingleton(_ => new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat));
                services.AddSingleton<IVadOnnxModel, SileroOnnx>();
                services.AddSingleton<IAudioFileCaching, DefaultAudioFileCaching>();
                services.AddSingleton<IAudioPromptPlayer, PromptAudioPlayer>();
                services.AddSingleton<ResourceManager>();
            });
        }

        public override bool BuildComponent()
        {
            #region FFmpeg
            if (MediaFactory.CheckFFmpegInstalled(out string ffmpegVersion))
            {
                this.Logger.LogInformation("FFmpeg 已安装，版本：{ffmpegVersion}", ffmpegVersion);
            }
            else
            {
                this.Logger.LogWarning("未找到 FFmpeg。");
                return false;
            }
            #endregion

            IVadOnnxModel vadOnnxModel = this.ServiceProvider.GetRequiredService<IVadOnnxModel>();
            if (!vadOnnxModel.Load(this.GetSelectedSetting("VAD", this.Config.ModelConfig)))
            {
                return false;
            }

            IAudioFileCaching audioFileCaching = this.ServiceProvider.GetRequiredService<IAudioFileCaching>();
            return audioFileCaching.Load();
        }

        public override void Dispose()
        {
            this.ServiceProvider.GetRequiredService<IVadOnnxModel>().Dispose();
            this.ServiceProvider.GetRequiredService<IAudioFileCaching>().Dispose();
            this.ServiceProvider.GetRequiredService<IAudioPromptPlayer>().Dispose();
        }

        private ModelSetting GetSelectedSetting(string selectedModelType, ModelConfig config)
        {
            string selectedModel = config.SelectedDefaultSettings[selectedModelType];
            Dictionary<string, string> setting = config.ConfiguredSettings[selectedModelType][selectedModel];
            return new ModelSetting
            {
                ModelName = selectedModel,
                Config = setting
            };
        }
    }
}
