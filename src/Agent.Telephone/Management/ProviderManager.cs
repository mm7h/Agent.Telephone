using System.ClientModel;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.ASR.Sherpa;
using Agent.Telephone.Providers.AudioProcessor;
using Agent.Telephone.Providers.CallControl;
using Agent.Telephone.Providers.Conversation;
using Agent.Telephone.Providers.LLM;
using Agent.Telephone.Providers.LLM.Agents;
using Agent.Telephone.Providers.LLM.Agents.Intent;
using Agent.Telephone.Providers.TTS.Huoshan;
using Agent.Telephone.Providers.TTS.Sherpa;
using Agent.Telephone.Providers.VAD.Native;
using Agent.Telephone.Providers.VAD.Sherpa;
using Agent.Telephone.Resources.Audio;
using Flurl.Http.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Management
{
    internal sealed class ProviderManager : BaseManager
    {
        private readonly ConversationProvider _conversationProvider;

        public ProviderManager(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            ConversationProvider conversationProvider,
            ILogger<ProviderManager> logger)
            : base(serviceProvider, config, logger)
        {
            this._conversationProvider = conversationProvider;
        }

        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            return builder.ConfigureServices((_, services) =>
            {
                services.AddSingleton<CallControlProvider>();
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
                CallControlProvider callControl =
                    this.ServiceProvider.GetRequiredService<CallControlProvider>();
                if (!callControl.Build(ModelSetting.Empty))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", callControl.ModelName);
                    return false;
                }

                if (!this._conversationProvider.Build(ModelSetting.Empty))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", this._conversationProvider.ModelName);
                    return false;
                }

                #region Vad
                IVad vad = this.ServiceProvider.GetRequiredKeyedService<IVad>(GlobalProviderNames.GLOBAL_VAD);
                if (vad.IsSherpaModel && !vad.Build(this.GetSelectedSherpaSetting("VAD", this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", vad.ModelName);
                    return false;
                }
                #endregion

                #region Asr
                IAsr asr = this.ServiceProvider.GetRequiredKeyedService<IAsr>(GlobalProviderNames.GLOBAL_ASR);
                if (asr.IsSherpaModel && !asr.Build(this.GetSelectedSherpaSetting("ASR", this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", asr.ModelName);
                    return false;
                }
                #endregion

                #region Tts
                ITts tts = this.ServiceProvider.GetRequiredKeyedService<ITts>(GlobalProviderNames.GLOBAL_TTS);
                if (tts.IsSherpaModel && !tts.Build(this.GetSelectedSherpaSetting("TTS", this.Config.ModelConfig)))
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

        public async Task<byte[]> DecodeAudioFileToPcmWaveAsync(
            string? path,
            CancellationToken cancellationToken = default)
        {
            IAudioProcessor audioProcessor = this.ServiceProvider
                .GetRequiredService<IAudioProcessor>();
            try
            {
                if (!audioProcessor.Build(ModelSetting.Empty))
                {
                    return [];
                }

                return await audioProcessor
                    .DecodeFileToPcmWaveAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                audioProcessor.Dispose();
            }
        }

        public async Task<bool> PlayPromptAsync(
            string? path,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            IAudioProcessor audioProcessor = this.ServiceProvider
                .GetRequiredService<IAudioProcessor>();
            try
            {
                return audioProcessor.Build(ModelSetting.Empty) &&
                    await audioProcessor.PlayFileAsync(
                        path,
                        mediaSession,
                        audioFormat,
                        cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                audioProcessor.Dispose();
            }
        }

        public override Task<bool> OnSIPDeviceRegisteredAsync(
            DeviceContext deviceContext,
            SIPTransport sipTransport,
            SIPRequest sipRequest)
        {
            return this.BuildForActiveCallAsync(deviceContext);
        }

        public async Task<bool> BuildForActiveCallAsync(DeviceContext deviceContext)
        {
            ActiveCallContext? activeCall = deviceContext.ActiveCall;
            if (activeCall is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理器。", deviceContext.DeviceId);
                return false;
            }
            PrivateProvider providers = activeCall.AIAgentContext.PrivateProvider;
            IAudioProcessor? pendingAudioProcessor = null;
            IVad? pendingVad = null;
            IAsr? pendingAsr = null;
            ILlm? pendingLlm = null;
            ITts? pendingTts = null;
            try
            {
                #region AudioProcessor Build
                pendingAudioProcessor = this.ServiceProvider.GetRequiredService<IAudioProcessor>();
                if (!pendingAudioProcessor.Build(ModelSetting.Empty))
                {
                    this.Logger.LogWarning("无法构建 {modelName} 提供程序。", pendingAudioProcessor.ModelName);
                    return false;
                }
                providers.SetAudioProcessor(pendingAudioProcessor);
                pendingAudioProcessor = null;
                #endregion

                #region VAD Build
                pendingVad = this.ServiceProvider.GetRequiredKeyedService<IVad>(ConvertToKebabCase(activeCall.AssistantConfig.VAD));
                if (!pendingVad.IsSherpaModel && !pendingVad.Build(this.GetConfiguredSetting("VAD", activeCall.AssistantConfig.VAD, this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingVad.ModelName);
                    return false;
                }
                providers.SetVad(pendingVad);
                pendingVad = null;
                #endregion

                #region ASR Build
                pendingAsr = this.ServiceProvider.GetRequiredKeyedService<IAsr>(ConvertToKebabCase(activeCall.AssistantConfig.ASR));
                if (!pendingAsr.IsSherpaModel && !pendingAsr.Build(this.GetConfiguredSetting("ASR", activeCall.AssistantConfig.ASR, this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingAsr.ModelName);
                    return false;
                }
                providers.SetAsr(pendingAsr);
                pendingAsr = null;
                #endregion

                #region LLM Build
                ModelSetting selectedIntentLLMModelSetting = this.GetConfiguredSetting("Intent", activeCall.AssistantConfig.Intent, this.Config.ModelConfig);
                string intentType = selectedIntentLLMModelSetting.Config.GetConfigValueOrDefault("Type", "None");

                ModelSetting selectedChatLLMModelSetting = this.GetConfiguredSetting("LLM", activeCall.AssistantConfig.LLM, this.Config.ModelConfig);
                selectedChatLLMModelSetting.Config.SetConfigValue("IntentType", intentType);
                selectedChatLLMModelSetting.Config.SetConfigValue("Prompt", activeCall.AssistantConfig.Prompt);
                int maximumTurns = this.GetMaximumMemoryTurns(activeCall.AssistantConfig);
                string? recentMemory = await this._conversationProvider.BuildRecentMemoryAsync(
                    activeCall.UserAor,
                    activeCall.AssistantConfig.DialingNumber,
                    maximumTurns,
                    activeCall.CallToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(recentMemory))
                {
                    selectedChatLLMModelSetting.Config.SetConfigValue("SummaryMemory", recentMemory);
                }
                pendingLlm = this.ServiceProvider.GetRequiredKeyedService<ILlm>(
                    ConvertToKebabCase(activeCall.AssistantConfig.LLM));

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
                    activeCall.AIAgentContext.PrivateProvider);

                if (!pendingLlm.Build(llmBuildConfig))
                {
                    this.Logger.LogError("无法为设备 {deviceId} 构建通用 LLM 模型。", deviceContext.DeviceId);
                    return false;
                }
                providers.SetLlm(pendingLlm);
                pendingLlm = null;
                #endregion

                #region TTS Build
                pendingTts = this.ServiceProvider.GetRequiredKeyedService<ITts>(ConvertToKebabCase(activeCall.AssistantConfig.TTS));
                if (!pendingTts.IsSherpaModel && !pendingTts.Build(this.GetConfiguredSetting("TTS", activeCall.AssistantConfig.TTS, this.Config.ModelConfig)))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingTts.ModelName);
                    return false;
                }
                providers.SetTts(pendingTts);
                pendingTts = null;
                #endregion

                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "设备 {deviceId} 的 Provider 初始化失败。", deviceContext.DeviceId);
                return false;
            }
            finally
            {
                pendingAudioProcessor?.Dispose();
                if (pendingVad is { IsSherpaModel: false })
                {
                    pendingVad.Dispose();
                }
                if (pendingAsr is { IsSherpaModel: false })
                {
                    pendingAsr.Dispose();
                }
                pendingLlm?.Dispose();
                if (pendingTts is { IsSherpaModel: false })
                {
                    pendingTts.Dispose();
                }
            }
        }

        private int GetMaximumMemoryTurns(AssistantConfig assistant)
        {
            int configuredMaximum = this.Config.MessageStoreConfig.RecentConversationTurns;
            if (this.Config.ModelConfig.ConfiguredSettings
                    .TryGetValue("Memory", out var memories) &&
                memories.TryGetValue(assistant.Memory, out var memoryConfig) &&
                memoryConfig.TryGetValue("MaximumTurns", out string? configuredText) &&
                int.TryParse(configuredText, out int parsed) &&
                parsed > 0)
            {
                configuredMaximum = parsed;
            }

            return Math.Max(1, configuredMaximum);
        }

        private ModelSetting GetSelectedSherpaSetting(string selectedModelType, ModelConfig config)
        {
            string selectedModel = config.SelectedDefaultSettings[selectedModelType];
            Dictionary<string, string> setting = config.ConfiguredSettings[selectedModelType][selectedModel];

            ModelSetting modelSetting = new ModelSetting
            {
                ModelName = selectedModel,
                Config = new Dictionary<string, string>(setting)
            };

            return modelSetting;
        }
        private ModelSetting GetConfiguredSetting(string selectedModelType, string selectedModel, ModelConfig config)
        {
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
            services.AddSingleton(_ => new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat));

            services.AddTransient<IAudioProcessor, DefaultAudioProcessor>();
        }
        #endregion

        #region VAD
        private static void RegisterVad(IServiceCollection services, ModelConfig config, string key)
        {
            string selectedModelName = ConvertToKebabCase(config.SelectedDefaultSettings["VAD"]);
            switch (selectedModelName)
            {
                case "sense-voice":
                    services.AddKeyedSingleton<IAsr, SenseVoice>(key);
                    break;
                case "paraformer":
                    services.AddKeyedSingleton<IAsr, Paraformer>(key);
                    break;
                case "silero-native":
                    services.AddKeyedTransient<IVad, SileroNative>(key);
                    break;
                default:
                    throw new ModelBuildException("Invalid asr model.");
            }
            foreach (var vadSettingItem in config.ConfiguredSettings["VAD"])
            {
                string modelName = ConvertToKebabCase(vadSettingItem.Key);
                switch (modelName)
                {
                    case "silero":
                        services.AddKeyedSingleton<IVad, Silero>(key);
                        break;
                    case "paraformer":
                        services.AddKeyedSingleton<IAsr, Paraformer>(key);
                        break;
                    case "silero-native":
                        services.AddKeyedTransient<IVad, SileroNative>(key);
                        break;
                    default:
                        throw new ModelBuildException("Invalid vad model.");
                }
            }
        }
        #endregion

        #region ASR
        private static void RegisterAsr(IServiceCollection services, ModelConfig config, string key)
        {
            string selectedModelName = ConvertToKebabCase(config.SelectedDefaultSettings["ASR"]);
            switch (selectedModelName)
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
            foreach (var asrSettingItem in config.ConfiguredSettings["ASR"])
            {
                string modelName = ConvertToKebabCase(asrSettingItem.Key);
                switch (modelName)
                {
                    case "sense-voice":
                        services.AddKeyedSingleton<IAsr, SenseVoice>(modelName);
                        services.AddKeyedSingleton<IAsr, SenseVoice>(key);
                        break;
                    case "paraformer":
                        services.AddKeyedSingleton<IAsr, Paraformer>(modelName);
                        services.AddKeyedSingleton<IAsr, Paraformer>(key);
                        break;
                    default:
                        throw new ModelBuildException("Invalid asr model.");
                }
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
            string selectedModelName = ConvertToKebabCase(config.SelectedDefaultSettings["TTS"]);
            switch (selectedModelName)
            {
                case "kokoro":
                    services.AddKeyedSingleton<ITts, Kokoro>(key);
                    break;
                case "huoshan-bidirection":
                    services.AddKeyedTransient<ITts, HuoshanBidirectionTTS>(key);
                    break;
                /*
                case "huoshan-unidirectional":
                    services.AddKeyedTransient<ITts, HuoshanUnidirectionalTTS>(modelName);
                    services.AddKeyedTransient<ITts, HuoshanUnidirectionalTTS>(key);
                    break;
                */
                case "huoshan-http":
                    services.AddSingleton(_ => new FlurlClientCache()
                    .Add(nameof(HuoshanHttpTTS), configure: builder =>
                    {
                        builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                    }));
                    services.AddKeyedTransient<ITts, HuoshanHttpTTS>(key);
                    break;
                case "huoshan-http-v3":
                    services.AddSingleton(_ => new FlurlClientCache()
                    .Add(nameof(HuoshanHttpV3TTS), configure: builder =>
                    {
                        builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                    }));
                    services.AddKeyedTransient<ITts, HuoshanHttpV3TTS>(key);
                    break;
                default:
                    throw new ModelBuildException("Invalid tts model.");
            }
            foreach (var ttsSettingItem in config.ConfiguredSettings["TTS"])
            {
                string modelName = ConvertToKebabCase(ttsSettingItem.Key);
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

        }
        #endregion

        #endregion
    }
}
