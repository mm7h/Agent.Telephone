using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.ASR.Sherpa;
using Agent.Telephone.Providers.AudioProcessor;
using Agent.Telephone.Providers.LLM;
using Agent.Telephone.Providers.LLM.Agents;
using Agent.Telephone.Providers.LLM.Agents.Intent;
using Agent.Telephone.Providers.TTS.Huoshan;
using Agent.Telephone.Providers.TTS.Sherpa;
using Agent.Telephone.Providers.VAD.Native;
using Agent.Telephone.Providers.VAD.Sherpa;
using Flurl.Http.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using SIPSorcery.SIP;
using System;
using System.ClientModel;

namespace Agent.Telephone.Management
{
    internal sealed class ProviderManager : BaseManager
    {
        public ProviderManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger<ProviderManager> logger)
            : base(serviceProvider, config, logger) { }

        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            return builder.ConfigureServices((_, services) =>
            {
                RegisterAudioProcessor(services);
                RegisterVad(services, config.ModelConfig, GlobalProviderNames.GLOBAL_VAD);
                RegisterAsr(services, config.ModelConfig, GlobalProviderNames.GLOBAL_ASR);
                RegisterLlm(services, config.ModelConfig);
                RegisterTts(services, config.ModelConfig, GlobalProviderNames.GLOBAL_TTS);

                services.AddSingleton<ProviderManager>();
            });
        }

        public override bool BuildComponent()
        {
            try
            {
                #region Vad
                IVad vad = this.ServiceProvider.GetRequiredKeyedService<IVad>(GlobalProviderNames.GLOBAL_VAD);
                if (vad.IsSherpaModel && !vad.Build(this.GetSelectedSetting("VAD", this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", vad.ModelName);
                    return false;
                }
                #endregion

                #region Asr
                IAsr asr = this.ServiceProvider.GetRequiredKeyedService<IAsr>(GlobalProviderNames.GLOBAL_ASR);
                if (asr.IsSherpaModel && !asr.Build(this.GetSelectedSetting("ASR", this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", asr.ModelName);
                    return false;
                }
                #endregion

                #region Tts
                ITts tts = this.ServiceProvider.GetRequiredKeyedService<ITts>(GlobalProviderNames.GLOBAL_TTS);
                if (tts.IsSherpaModel && !tts.Build(this.GetSelectedSetting("TTS", this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", tts.ModelName);
                    return false;
                }
                #endregion

                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "构建提供程序组件失败。");
                return false;
            }
        }

        public override Task<bool> OnSIPDeviceRegisteredAsync(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            if (deviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return Task.FromResult(false);
            }
            PrivateProvider providers = deviceContext.ActiveCall.AIAgentContext.PrivateProvider;
            try
            {
                var audioProcessor = this.ServiceProvider.GetRequiredService<IAudioProcessor>();
                var vad = this.ServiceProvider.GetRequiredService<IVad>();
                var asr = this.ServiceProvider.GetRequiredService<IAsr>();
                var llm = this.ServiceProvider.GetRequiredService<ILlm>();
                var tts = this.ServiceProvider.GetRequiredService<ITts>();


                providers.SetAudioProcessor(audioProcessor);
                providers.SetVad(vad);
                providers.SetAsr(asr);
                providers.SetLlm(llm);
                providers.SetTts(tts);
                return Task.FromResult(true);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "设备 {deviceId} 的 Provider 初始化失败。", deviceContext.DeviceId);
                return Task.FromResult(false);
            }
        }
        private ModelSetting GetSelectedSetting(string selectedModelType, ModelConfig config)
        {
            string selectedModel = config.SelectedSettings[selectedModelType];
            Dictionary<string, string> setting = config.ConfiguredSettings[selectedModelType][selectedModel];

            ModelSetting modelSetting = new ModelSetting
            {
                ModelName = selectedModel,
                Config = new Dictionary<string, string>(setting)
            };

            return modelSetting;
        }

        #region Register providers

        #region AudioProcessor
        private static void RegisterAudioProcessor(IServiceCollection services)
        {
            services.AddTransient<IAudioProcessor, DefaultAudioProcessor>();
        }

        public void BuildAudioProcessor(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return;
            }
            IAudioProcessor audioProcessor = this.ServiceProvider.GetRequiredService<IAudioProcessor>();
            if (!audioProcessor.Build(ModelSetting.Empty))
            {
                this.Logger.LogWarning("无法构建 {modelName} 提供程序。", audioProcessor.ModelName);
            }
            deviceContext.ActiveCall.AIAgentContext.PrivateProvider.SetAudioProcessor(audioProcessor);
        }
        #endregion

        #region VAD
        private static void RegisterVad(IServiceCollection services, ModelConfig config, string key)
        {
            string modelName = ConvertToKebabCase(config.SelectedSettings["VAD"]);
            switch (modelName)
            {
                case "silero":
                    services.AddKeyedSingleton<IVad, Silero>(key);
                    break;
                case "silero-native":
                    services.AddKeyedTransient<IVad, SileroNative>(modelName);
                    services.AddKeyedTransient<IVad, SileroNative>(key);
                    break;
                default:
                    throw new ModelBuildException("Invalid vad model.");
            }
        }
        #endregion

        #region ASR
        private static void RegisterAsr(IServiceCollection services, ModelConfig config, string key)
        {
            string modelName = ConvertToKebabCase(config.SelectedSettings["ASR"]);
            switch (modelName)
            {
                case "sense-voice":
                    services.AddKeyedSingleton<IAsr, SenseVoice>(key);
                    break;
                case "paraformer":
                    services.AddKeyedSingleton<IAsr, Paraformer>(key);
                    break;
                default:
                    throw new ModelBuildException("Invalid asr model.");
            }
        }
        #endregion

        #region LLM
        private static void RegisterLlm(IServiceCollection services, ModelConfig config)
        {
            foreach (var llmSettingItem in config.ConfiguredSettings["LLM"])
            {
                string? endPoint = llmSettingItem.Value.GetConfigValueOrDefault("BaseUrl");
                string? apiKey = llmSettingItem.Value.GetConfigValueOrDefault("ApiKey");
                string? modelId = llmSettingItem.Value.GetConfigValueOrDefault("ModelName");

                if (string.IsNullOrWhiteSpace(endPoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelId))
                {
                    throw new ModelBuildException($"Invalid llm model setting, endPoint: {endPoint}, apiKey: {apiKey}, modelId: {modelId}.");
                }

                string llmProviderKey = $"LLM_{llmSettingItem.Key}";

                // 注册 IChatClient，使用 MEAI OpenAI 适配器
                services.AddKeyedSingleton<IChatClient>(llmProviderKey, (_, _) =>
                {
                    OpenAIClient openAIClient = new OpenAIClient(
                        new ApiKeyCredential(apiKey),
                        new OpenAIClientOptions { Endpoint = new Uri(endPoint) });
                    return openAIClient.GetChatClient(modelId).AsIChatClient();
                });
            }

            services.AddKeyedTransient<IAgent, InputAgent>(SubAgentNames.InputAgent);
            services.AddKeyedTransient<IAgent, IntentDetectionAgent>(SubAgentNames.IntentDetectionAgent);
            services.AddKeyedTransient<IAgent, FunctionCallAgent>(SubAgentNames.FunctionCallAgent);
            services.AddKeyedTransient<IAgent, IntentResponseAgent>(SubAgentNames.IntentResponseAgent);
            services.AddKeyedTransient<IAgent, ChatAgent>(SubAgentNames.ChatAgent);
            services.AddKeyedTransient<IAgent, OutputAgent>(SubAgentNames.OutputAgent);
            services.AddTransient<ILlm, GenericOpenAI>();
        }
        #endregion

        #region TTS
        private static void RegisterTts(IServiceCollection services, ModelConfig config, string key)
        {
            string modelName = ConvertToKebabCase(config.SelectedSettings["TTS"]);
            switch (modelName)
            {
                case "kokoro":
                    services.AddKeyedSingleton<ITts, Kokoro>(key);
                    break;
                case "huoshan-bidirection":
                    services.AddKeyedTransient<ITts, HuoshanBidirectionTTS>(modelName);
                    services.AddKeyedTransient<ITts, HuoshanBidirectionTTS>(key);
                    break;
                case "huoshan-http":
                    services.AddSingleton(_ => new FlurlClientCache()
                    .Add(nameof(HuoshanHttpTTS), configure: builder =>
                    {
                        builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                    }));
                    services.AddKeyedTransient<ITts, HuoshanHttpTTS>(modelName);
                    services.AddKeyedTransient<ITts, HuoshanHttpTTS>(key);
                    break;
                case "huoshan-http-v3":
                    services.AddSingleton(_ => new FlurlClientCache()
                    .Add(nameof(HuoshanHttpV3TTS), configure: builder =>
                    {
                        builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                    }));
                    services.AddKeyedTransient<ITts, HuoshanHttpV3TTS>(modelName);
                    services.AddKeyedTransient<ITts, HuoshanHttpV3TTS>(key);
                    break;
                default:
                    throw new ModelBuildException("Invalid tts model.");
            }
        }
        #endregion

        #endregion


        private bool RegisterGlobalProviders(DeviceContext deviceContext)
        {
            bool vadRegistered = this.RegisterGlobalVadProviders(deviceContext);
            bool asrRegistered = this.RegisterGlobalAsrProviders(deviceContext);
            bool llmRegistered = this.RegisterGlobalLlmProviders(deviceContext);
            bool ttsRegistered = this.RegisterGlobalTtsProviders(deviceContext);

            return vadRegistered && asrRegistered && llmRegistered && ttsRegistered;
        }

        private bool RegisterGlobalVadProviders(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return false;
            }
            IVad genericVad = this.ServiceProvider.GetRequiredKeyedService<IVad>(GlobalProviderNames.GLOBAL_VAD);
            if (!genericVad.IsSherpaModel && !genericVad.Build(this.GetSelectedSetting("VAD", this.Config.ModelConfig)))
            {
                this.Logger.LogError("无法构建 {modelName} 提供程序。", genericVad.ModelName);
                return false;
            }
            deviceContext.ActiveCall.AIAgentContext.PrivateProvider.SetVad(genericVad);
            this.Logger.LogInformation("设备 {deviceId} 的通用 VAD {modeName} 模型已初始化。", deviceContext.DeviceId, genericVad.ModelName);
            return true;
        }

        private bool RegisterGlobalAsrProviders(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return false;
            }
            IAsr genericAsr = this.ServiceProvider.GetRequiredKeyedService<IAsr>(GlobalProviderNames.GLOBAL_ASR);
            if (!genericAsr.IsSherpaModel && !genericAsr.Build(this.GetSelectedSetting("ASR", this.Config.ModelConfig)))
            {
                this.Logger.LogError("无法构建 {modelName} 提供程序。", genericAsr.ModelName);
                return false;
            }
            deviceContext.ActiveCall.AIAgentContext.PrivateProvider.SetAsr(genericAsr);
            this.Logger.LogInformation("设备 {deviceId} 的通用 ASR {modeName} 模型已初始化。", deviceContext.DeviceId, genericAsr.ModelName);
            return true;
        }

        private bool RegisterGlobalLlmProviders(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return false;
            }
            ILlm genericLlm = this.ServiceProvider.GetRequiredService<ILlm>();

            ModelSetting selectedIntentLLMModelSetting = this.GetSelectedSetting("Intent", this.Config.ModelConfig);
            string intentType = selectedIntentLLMModelSetting.Config.GetConfigValueOrDefault("Type", "None");

            ModelSetting selectedChatLLMModelSetting = this.GetSelectedSetting("LLM", this.Config.ModelConfig);
            selectedChatLLMModelSetting.Config.SetConfigValue("IntentType", intentType);

            ModelSetting intentResponseAgentSetting = new ModelSetting
            {
                ModelName = selectedIntentLLMModelSetting.ModelName,
                Config = new Dictionary<string, string>(selectedIntentLLMModelSetting.Config)
            };

            ModelSetting inputAgentSetting = new ModelSetting
            {
                ModelName = SubAgentNames.InputAgent,
                Config = new Dictionary<string, string>
                {
                   { "IntentType", intentType }
                }
            };

            Dictionary<string, ModelSetting> agentSettings = new Dictionary<string, ModelSetting>
            {
                { SubAgentNames.InputAgent, inputAgentSetting },
                { SubAgentNames.IntentDetectionAgent, selectedIntentLLMModelSetting },
                { SubAgentNames.FunctionCallAgent, ModelSetting.Empty },
                { SubAgentNames.IntentResponseAgent, intentResponseAgentSetting },
                { SubAgentNames.ChatAgent, selectedChatLLMModelSetting },
                { SubAgentNames.OutputAgent, ModelSetting.Empty },
            };

            LLMBuildConfig llmBuildConfig = new LLMBuildConfig(
                agentSettings,
                deviceContext.ActiveCall.AIAgentContext.PrivateProvider);

            if (!genericLlm.Build(llmBuildConfig))
            {
                this.Logger.LogError("无法为设备 {deviceId} 构建通用 LLM 模型。", deviceContext.DeviceId);
                return false;
            }
            deviceContext.ActiveCall.AIAgentContext.PrivateProvider.SetLlm(genericLlm);

            this.Logger.LogInformation("设备 {deviceId} 的通用 LLM 模型已初始化。", deviceContext.DeviceId);
            return true;
        }

        private bool RegisterGlobalTtsProviders(DeviceContext deviceContext)
        {
            if (deviceContext.ActiveCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return false;
            }
            ITts genericTts = this.ServiceProvider.GetRequiredKeyedService<ITts>(GlobalProviderNames.GLOBAL_TTS);
            if (!genericTts.IsSherpaModel && !genericTts.Build(this.GetSelectedSetting("TTS", this.Config.ModelConfig)))
            {
                this.Logger.LogError("无法构建 {modelName} 提供程序。", genericTts.ModelName);
                return false;
            }
            deviceContext.ActiveCall.AIAgentContext.PrivateProvider.SetTts(genericTts);
            this.Logger.LogInformation("设备 {deviceId} 的通用 TTS {modeName} 模型已初始化。", deviceContext.DeviceId, genericTts.ModelName);
            return true;
        }
    }
}
