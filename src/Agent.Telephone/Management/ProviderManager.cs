using System.ClientModel;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.ASR.Aliyun;
using Agent.Telephone.Providers.ASR.Sherpa;
using Agent.Telephone.Providers.AudioProcessor;
using Agent.Telephone.Providers.CallControl;
using Agent.Telephone.Providers.CallControl.Reservations;
using Agent.Telephone.Providers.Dtmf;
using Agent.Telephone.Providers.LLM;
using Agent.Telephone.Providers.LLM.AIContextProviders;
using Agent.Telephone.Providers.LLM.Agents;
using Agent.Telephone.Providers.LLM.Agents.Intent;
using Agent.Telephone.Providers.OfflineDialogue;
using Agent.Telephone.Providers.TTS.Aliyun;
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

namespace Agent.Telephone.Management
{
    internal sealed class ProviderManager : BaseManager
    {
        public ProviderManager(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            ILogger<ProviderManager> logger)
            : base(serviceProvider, config, logger)
        {
        }

        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            return builder.ConfigureServices((_, services) =>
            {
                RegisterAudioProcessor(services);
                RegisterVad(services, config.ModelConfig);
                RegisterAsr(services, config.ModelConfig);
                RegisterLlm(services, config.ModelConfig);
                RegisterTts(services, config.ModelConfig);
                RegisterOfflineDialogue(services, config.ModelConfig);
                RegisterCallControl(services, config.ModelConfig);
                services.AddTransient<IDtmfInput, DefaultDtmfInput>();

                services.AddSingleton<ProviderManager>();
            });
        }

        public override bool BuildComponent()
        {
            try
            {
                #region Vad
                string selectedVadModel = this.Config.ModelConfig.SelectedDefaultSettings["VAD"];
                if (SherpaModels.VadModels.Contains(selectedVadModel, StringComparer.OrdinalIgnoreCase))
                {
                    IVad vad = this.ServiceProvider.GetRequiredKeyedService<IVad>(
                        ConvertToKebabCase(selectedVadModel));
                    if (!vad.Build(this.GetSelectedSherpaSetting("VAD", this.Config.ModelConfig)))
                    {
                        this.Logger.LogError("无法构建 {modelName} 提供程序。", vad.ModelName);
                        return false;
                    }
                }
                #endregion

                #region Asr
                string selectedAsrModel = this.Config.ModelConfig.SelectedDefaultSettings["ASR"];
                if (SherpaModels.AsrModels.Contains(selectedAsrModel, StringComparer.OrdinalIgnoreCase))
                {
                    IAsr asr = this.ServiceProvider.GetRequiredKeyedService<IAsr>(
                        ConvertToKebabCase(selectedAsrModel));
                    if (!asr.Build(this.GetSelectedSherpaSetting("ASR", this.Config.ModelConfig)))
                    {
                        this.Logger.LogError("无法构建 {modelName} 提供程序。", asr.ModelName);
                        return false;
                    }
                }
                #endregion

                #region Tts
                string selectedTtsModel = this.Config.ModelConfig.SelectedDefaultSettings["TTS"];
                if (SherpaModels.TtsModels.Contains(selectedTtsModel, StringComparer.OrdinalIgnoreCase))
                {
                    ITts tts = this.ServiceProvider.GetRequiredKeyedService<ITts>(
                        ConvertToKebabCase(selectedTtsModel));
                    if (!tts.Build(this.GetSelectedSherpaSetting("TTS", this.Config.ModelConfig)))
                    {
                        this.Logger.LogError("无法构建 {modelName} 提供程序。", tts.ModelName);
                        return false;
                    }
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
            IOfflineDialogue? pendingOfflineDialogue = null;
            ICallControl? pendingCallControl = null;
            IDtmfInput? pendingDtmfInput = null;
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
                ITelephoneStore telephoneStore = this.ServiceProvider.GetRequiredService<ITelephoneStore>();
                IReadOnlyList<ConversationMessage> conversationMessages = await telephoneStore.GetConversationMessagesAsync(
                    activeCall.UserAor,
                    activeCall.DialedNumber!,
                    activeCall.CallToken);
                activeCall.AIAgentContext.LoadPersistedChatHistory(conversationMessages);
                pendingLlm = this.ServiceProvider.GetRequiredService<ILlm>();

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

                LLMBuildConfig llmBuildConfig = new LLMBuildConfig(agentSettings, activeCall.AIAgentContext.PrivateProvider, activeCall.AssistantConfig.LLMResponseTimeoutSeconds);

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
                ModelSetting ttsModelSetting = this.GetConfiguredSetting("TTS", activeCall.AssistantConfig.TTS, this.Config.ModelConfig);
                this.RandomSelectTTSSpeaker(ttsModelSetting, activeCall.AssistantConfig);
                if (!pendingTts.IsSherpaModel && !pendingTts.Build(ttsModelSetting))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingTts.ModelName);
                    return false;
                }
                providers.SetTts(pendingTts);
                pendingTts = null;
                #endregion

                #region Offline Dialogue
                pendingOfflineDialogue = this.ServiceProvider.GetRequiredService<IOfflineDialogue>();
                if (!pendingOfflineDialogue.Build(ModelSetting.Empty))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingOfflineDialogue.ModelName);
                    return false;
                }
                providers.SetOfflineDialogue(pendingOfflineDialogue);
                pendingOfflineDialogue = null;
                #endregion

                #region Call Control
                pendingCallControl = this.ServiceProvider.GetRequiredService<ICallControl>();
                if (!pendingCallControl.Build(this.Config.AssistantConfigs))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingCallControl.ModelName);
                    return false;
                }
                providers.SetCallControl(pendingCallControl);
                pendingCallControl = null;
                #endregion

                #region Dtmf Input
                pendingDtmfInput = this.ServiceProvider.GetRequiredService<IDtmfInput>();
                if (!pendingDtmfInput.Build(ModelSetting.Empty))
                {
                    this.Logger.LogError("无法构建 {modelName} 提供程序。", pendingDtmfInput.ModelName);
                    return false;
                }
                providers.SetDtmfInput(pendingDtmfInput);
                pendingDtmfInput = null; 
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
                pendingOfflineDialogue?.Dispose();
                pendingCallControl?.Dispose();
                pendingDtmfInput?.Dispose();
            }
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

        internal void RandomSelectTTSSpeaker(ModelSetting existingModelSetting, AssistantConfig assistantConfig)
        {
            if (assistantConfig.TTSSettings.Count == 0)
            {
                return;
            }

            Dictionary<string, string> selectedSettings = assistantConfig.TTSSettings[
                Random.Shared.Next(assistantConfig.TTSSettings.Count)];
            foreach (KeyValuePair<string, string> setting in selectedSettings)
            {
                existingModelSetting.Config[setting.Key] = setting.Value;
            }
        }

        #region Register providers

        #region AudioProcessor
        private static void RegisterAudioProcessor(IServiceCollection services)
        {
            services.AddTransient<IAudioProcessor, DefaultAudioProcessor>();
        }
        #endregion

        #region VAD
        private static void RegisterVad(IServiceCollection services, ModelConfig config)
        {
            foreach (var vadSettingItem in config.ConfiguredSettings["VAD"])
            {
                string modelName = ConvertToKebabCase(vadSettingItem.Key);
                switch (modelName)
                {
                    case "silero":
                        services.AddKeyedSingleton<IVad, Silero>(modelName);
                        break;
                    case "silero-native":
                        services.AddKeyedTransient<IVad, SileroNative>(modelName);
                        break;
                    default:
                        throw new ModelBuildException("Invalid vad model.");
                }
            }
        }
        #endregion

        #region ASR
        private static void RegisterAsr(IServiceCollection services, ModelConfig config)
        {
            foreach (var asrSettingItem in config.ConfiguredSettings["ASR"])
            {
                string modelName = ConvertToKebabCase(asrSettingItem.Key);
                switch (modelName)
                {
                    case "sense-voice":
                        services.AddKeyedSingleton<IAsr, SenseVoice>(modelName);
                        break;
                    case "paraformer":
                        services.AddKeyedSingleton<IAsr, Paraformer>(modelName);
                        break;
                    case "aliyun-realtime":
                        services.AddKeyedTransient<IAsr, AliyunRealtimeASR>(modelName);
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
            services.AddTransient<SessionChatHistoryProvider>();
        }
        #endregion

        #region TTS
        private static void RegisterTts(IServiceCollection services, ModelConfig config)
        {
            foreach (var ttsSettingItem in config.ConfiguredSettings["TTS"])
            {
                string modelName = ConvertToKebabCase(ttsSettingItem.Key);
                switch (modelName)
                {
                    case "kokoro":
                        services.AddKeyedSingleton<ITts, Kokoro>(modelName);
                        break;
                    case "huoshan-bidirection":
                        services.AddKeyedTransient<ITts, HuoshanBidirectionTTS>(modelName);
                        break;
                    case "aliyun-realtime-t-t-s":
                    case "aliyun-realtime-tts":
                        services.AddKeyedTransient<ITts, AliyunRealtimeTTS>(modelName);
                        break;
                    case "huoshan-http":
                        services.AddKeyedSingleton<IFlurlClientCache>(nameof(HuoshanHttpTTS), (_, _) => new FlurlClientCache()
                        .Add(nameof(HuoshanHttpTTS), configure: builder =>
                        {
                            builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                        }));
                        services.AddKeyedTransient<ITts, HuoshanHttpTTS>(modelName);
                        break;
                    case "huoshan-http-v3":
                        services.AddKeyedSingleton<IFlurlClientCache>(nameof(HuoshanHttpV3TTS), (_, _) => new FlurlClientCache()
                        .Add(nameof(HuoshanHttpV3TTS), configure: builder =>
                        {
                            builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                        }));
                        services.AddKeyedTransient<ITts, HuoshanHttpV3TTS>(modelName);
                        break;
                    case "aliyun-http":
                        services.AddKeyedSingleton<IFlurlClientCache>(nameof(AliyunHttpTTS), (_, _) => new FlurlClientCache()
                        .Add(nameof(AliyunHttpTTS), configure: builder =>
                        {
                            builder.Settings.JsonSerializer = new DefaultJsonSerializer(JsonHelper.OPTIONS);
                        }));
                        services.AddKeyedTransient<ITts, AliyunHttpTTS>(modelName);
                        break;
                    default:
                        throw new ModelBuildException("Invalid tts model.");
                }
            }

        }
        #endregion

        private static void RegisterCallControl(IServiceCollection services, ModelConfig config)
        {
            services.AddSingleton<TransferReservationRegistry>();
            services.AddTransient<ICallControl, AssistantRoleControl>();
        }

        private static void RegisterOfflineDialogue(IServiceCollection services, ModelConfig config)
        {
            services.AddTransient<IOfflineDialogue, DefaultOfflineDialogue>();
        }

        #endregion
    }
}
