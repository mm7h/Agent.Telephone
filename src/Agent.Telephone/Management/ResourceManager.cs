using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Resources;
using Agent.Telephone.Resources.AudioFileCaching;
using Agent.Telephone.Resources.Editors;
using Agent.Telephone.Resources.FileEncoders;
using Agent.Telephone.Resources.OnnxModels;
using Agent.Telephone.Resources.OnnxModels.VAD;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
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
                services.AddSingleton(_ => new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat));


                services.AddSingleton<IVadOnnxModel, SileroOnnx>();
                services.AddSingleton<IAudioFileEncoder, FFmpegFileEncoder>();
                services.AddSingleton<IAudioEditor, AudioEditor>();
                services.AddSingleton<IAudioFileCaching, DefaultAudioFileCaching>();

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

            #region AudioFileEncoder
            IAudioFileEncoder audioFileEncoder = this.ServiceProvider.GetRequiredService<IAudioFileEncoder>();
            if (!audioFileEncoder.Load(ModelSetting.Empty))
            {
                return false;
            }
            #endregion

            #region AudioEditor
            IAudioEditor audioEditor = this.ServiceProvider.GetRequiredService<IAudioEditor>();
            if (!audioEditor.Load(ModelSetting.Empty))
            {
                return false;
            } 
            #endregion

            #region AudioFileCaching
            IAudioFileCaching audioFileCaching = this.ServiceProvider.GetRequiredService<IAudioFileCaching>();

            AudioFileCachingBuildConfig audioFileCachingBuildConfig = new (this.Config.PromptMediaPath, this.Config.PromptMediaConfigs);
            if (!audioFileCaching.Load(audioFileCachingBuildConfig))
            {
                return false;
            }
            #endregion

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
                this.ServiceProvider.GetRequiredService<IAudioFileEncoder>(),
                this.ServiceProvider.GetRequiredService<IAudioEditor>(),
                this.ServiceProvider.GetRequiredService<IAudioFileCaching>()
            };

            foreach (IDisposable resource in resources)
            {
                resource.Dispose();
            }
        }
    }
}
