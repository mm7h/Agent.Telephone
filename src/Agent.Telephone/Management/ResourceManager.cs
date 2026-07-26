using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Resources;
using Agent.Telephone.Resources.Audio;
using Agent.Telephone.Resources.Editors;
using Agent.Telephone.Resources.FileEncoders;
using Agent.Telephone.Resources.OnnxModels;
using Agent.Telephone.Resources.OnnxModels.VAD;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.FFmpeg;

namespace Agent.Telephone.Management
{
    internal class ResourceManager : BaseManager
    {
        public ResourceManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger<ResourceManager> logger) : base(serviceProvider, config, logger)
        {
        }
        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_FATAL, config.SIPConfig.FFmpegPath);
            return builder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<IVadOnnxModel, SileroOnnx>();
                services.AddSingleton<IAudioFileEncoder, FFmpegFileEncoder>();
                services.AddSingleton<IAudioEditor, AudioEditor>();
                services.AddSingleton<FfmpegAudioPlayer>();

                services.AddSingleton<ResourceManager>();
            });
        }

        public override bool BuildComponent()
        {
            #region Onnx models
            IVadOnnxModel vadOnnxModel = this.ServiceProvider.GetRequiredService<IVadOnnxModel>();
            if (!vadOnnxModel.Load(this.GetSelectedSetting("VAD", this.Config.ModelConfig)))
            {
                return false;
            }
            #endregion

            FfmpegAudioPlayer filePlayer = this.ServiceProvider.GetRequiredService<FfmpegAudioPlayer>();
            if (!filePlayer.Load(ModelSetting.Empty))
            {
                return false;
            }

            return FFmpegInit.EnsureBinariesRegistered();
        }
        private ModelSetting GetSelectedSetting(string selectedModelType, ModelConfig config)
        {
            string selectedModel = config.SelectedDefaultSettings[selectedModelType];
            Dictionary<string, string> setting = config.ConfiguredSettings[selectedModelType][selectedModel];

            ModelSetting modelSetting = new ModelSetting
            {
                ModelName = selectedModel,
                Config = setting
            };

            return modelSetting;
        }

        public override void Dispose()
        {
            IList<IDisposable> resources = new List<IDisposable>
            {
                this.ServiceProvider.GetRequiredService<IVadOnnxModel>(),
                this.ServiceProvider.GetRequiredService<FfmpegAudioPlayer>()
            };

            foreach (IDisposable resource in resources)
            {
                resource.Dispose();
            }
        }
    }
}
