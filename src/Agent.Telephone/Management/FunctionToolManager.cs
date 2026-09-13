using System.ComponentModel;
using System.Reflection;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Models;
using Agent.Telephone.FunctionTools;
using Agent.Telephone.FunctionTools.Adapters;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.CallControl;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Management
{
    internal class FunctionToolManager : BaseManager
    {
        private const DtmfKey SUPPORTED_DTMF_KEYS =
            DtmfKey.Zero | DtmfKey.One | DtmfKey.Two | DtmfKey.Three |
            DtmfKey.Four | DtmfKey.Five | DtmfKey.Six | DtmfKey.Seven |
            DtmfKey.Eight | DtmfKey.Nine | DtmfKey.Star | DtmfKey.Pound;
        private const string DTMF_TOOL_INSTRUCTION =
            "系统会在调用后自动播放按键提示并等待一次按键结果。识别到需求后直接调用此工具；不要在参数中提供按键结果，也不要自行播报菜单。";

        private readonly ILoggerFactory _loggerFactory;

        private readonly List<FunctionTool> _globalFunctionTools;
        private readonly Dictionary<Type, IEnumerable<FunctionToolMethodMetadata>> _globalFunctionToolMethodMetadata;
        private readonly Dictionary<Type, IEnumerable<FunctionToolMethodMetadata>> _privateFunctionToolMethodMetadata;

        private bool _hasFunctionTools = false;

        public FunctionToolManager(
            ILoggerFactory loggerFactory,
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            ILogger<FunctionToolManager> logger) : base(serviceProvider, config, logger)
        {
            this._loggerFactory = loggerFactory;

            this._globalFunctionTools = [];
            this._globalFunctionToolMethodMetadata = [];
            this._privateFunctionToolMethodMetadata = [];
        }

        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<FunctionToolManager>();
            });
        }

        #region Build Components
        public override bool BuildComponent()
        {
            try
            {
                List<IFunctionTool> globalFunctionTools = this.ServiceProvider.GetServices<IFunctionTool>().ToList();
                List<IPrivateFunctionTool> privateFunctionTools = this.ServiceProvider.GetServices<IPrivateFunctionTool>().ToList();

                foreach (IFunctionTool item in globalFunctionTools)
                {
                    if (item is FunctionTool instance)
                    {
                        instance.Logger = this._loggerFactory.CreateLogger(instance.GetType());
                        instance.ServerInfo = this.CreateServerInfoAdapter();

                        Type instanceType = instance.GetType();

                        this._globalFunctionToolMethodMetadata.Add(
                            instanceType,
                            this.ExtractTypeMetadata(instance, instanceType).ToArray());
                        instance.OnFunctionToolInitializedAsync().AsTask().GetAwaiter().GetResult();

                        this._globalFunctionTools.Add(instance);
                    }
                }

                foreach (IPrivateFunctionTool instance in privateFunctionTools)
                {
                    Type instanceType = instance.GetType();
                    this._privateFunctionToolMethodMetadata.Add(
                        instanceType,
                        this.ExtractTypeMetadata(instance, instanceType).ToArray());
                    if (instance is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }

                this._hasFunctionTools = this._globalFunctionToolMethodMetadata.Any() || this._privateFunctionToolMethodMetadata.Any();
                if (!this.ValidateDtmfToolDefinitions() || !this.ValidateAllowedTools())
                {
                    return false;
                }

                return true;
            }
            catch (AggregateException ae)
            {
                foreach (Exception inner in ae.InnerExceptions)
                {
                    this.Logger.LogError(inner, "FunctionToolManager.BuildComponent 并行任务失败");
                }
                this.ReleaseGlobalTools();
                return false;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "FunctionToolManager.BuildComponent 失败");
                this.ReleaseGlobalTools();
                return false;
            }
        }
        #endregion

        #region On Device Connecting
        public override Task<bool> OnSIPDeviceRegisteredAsync(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            return this.BuildForActiveCallAsync(deviceContext);
        }

        public async Task<bool> BuildForActiveCallAsync(DeviceContext deviceContext)
        {
            if (!this._hasFunctionTools)
            {
                this.Logger.LogWarning("没有注册任何 FunctionTool，无法为设备 {DeviceId} 提供功能工具", deviceContext.DeviceId);
                return true;
            }
            ActiveCallContext? activeCall = deviceContext.ActiveCall;
            if (activeCall is null)
            {
                this.Logger.LogWarning("设备 {DeviceId} 没有活动呼叫，无法为其注册 FunctionTool", deviceContext.DeviceId);
                return true;
            }
            IAssistantControl assistantControl = new AssistantControlAdapter(activeCall);
            foreach (FunctionTool instance in this._globalFunctionTools)
            {
                if (!this._globalFunctionToolMethodMetadata.TryGetValue(instance.GetType(), out IEnumerable<FunctionToolMethodMetadata>? methodMetas))
                {
                    continue;
                }

                foreach (FunctionToolMethodMetadata methodMeta in methodMetas.Where(method => this.IsAllowed(activeCall.AssistantConfig, instance.GetType(), method)))
                {
                    FunctionToolRegistration registration = this.BuildRegistration(instance, methodMeta, activeCall.AIAgentContext);
                    activeCall.AIAgentContext.PrivateProvider.AddFunctionToolRegistration(registration);
                }
            }

            Dictionary<Type, IPrivateFunctionTool> privateFunctionTools = this.ServiceProvider.GetServices<IPrivateFunctionTool>().ToDictionary(i => i.GetType());
            bool initializedToolsOwned = false;
            List<PrivateFunctionTool> initializedTools = [];
            try
            {
                activeCall.CallToken.ThrowIfCancellationRequested();

                foreach (var item in privateFunctionTools)
                {
                    activeCall.CallToken.ThrowIfCancellationRequested();
                    if (!this._privateFunctionToolMethodMetadata.TryGetValue(item.Key, out var methodMetas))
                    {
                        this.Logger.LogWarning("未找到私有 FunctionTool {ToolType} 的方法元数据，无法为设备 {DeviceId} 注册", item.Key.FullName, deviceContext.DeviceId);
                        continue;
                    }

                    if (item.Value is not PrivateFunctionTool instance)
                    {
                        continue;
                    }

                    FunctionToolMethodMetadata[] allowedMethods = methodMetas
                        .Where(method => this.IsAllowed(activeCall.AssistantConfig, item.Key, method))
                        .ToArray();
                    if (allowedMethods.Length == 0)
                    {
                        continue;
                    }

                    instance.Logger = this._loggerFactory.CreateLogger(instance.GetType());
                    instance.ServerInfo = this.CreateServerInfoAdapter();
                    instance.DeviceContext = new SessionContextAdapter(deviceContext);
                    instance.CallControl = assistantControl;

                    initializedTools.Add(instance);
                    await instance.OnFunctionToolInitializedAsync();
                    activeCall.CallToken.ThrowIfCancellationRequested();
                    await instance.OnDeviceConnectedAsync();
                    activeCall.CallToken.ThrowIfCancellationRequested();

                    foreach (FunctionToolMethodMetadata methodMeta in allowedMethods)
                    {
                        FunctionToolRegistration registration = this.BuildRegistration(instance, methodMeta, activeCall.AIAgentContext);
                        activeCall.AIAgentContext.PrivateProvider.AddFunctionToolRegistration(instance, registration);
                    }
                }

                activeCall.AIAgentContext.PrivateProvider.SetPrivateFunctionToolLifetime(
                    new PrivateFunctionToolLifetime(initializedTools, this.Logger));
                initializedToolsOwned = true;
                return true;
            }
            catch
            {
                if (!initializedToolsOwned)
                {
                    try
                    {
                        await FunctionToolManager.ReleasePrivateToolsAsync(initializedTools);
                    }
                    catch (Exception releaseException)
                    {
                        this.Logger.LogError(releaseException, "回滚通话级 FunctionTool 初始化时失败。");
                    }
                }
                throw;
            }
            finally
            {
                foreach (IPrivateFunctionTool tool in privateFunctionTools.Values)
                {
                    if (tool is IDisposable disposable &&
                        (tool is not PrivateFunctionTool privateTool ||
                         !initializedTools.Contains(privateTool)))
                    {
                        try
                        {
                            disposable.Dispose();
                        }
                        catch (Exception disposeException)
                        {
                            this.Logger.LogError(disposeException, "释放未使用的通话级 FunctionTool {ToolType} 时失败。", tool.GetType().FullName);
                        }
                    }
                }
            }
        }

        #endregion

        private IEnumerable<FunctionToolMethodMetadata> ExtractTypeMetadata(object? instance, Type toolType)
        {
            foreach (MethodInfo method in toolType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static m => !m.IsSpecialName)
                .Where(static m =>
                {
                    MethodInfo baseDefinition = m.GetBaseDefinition();
                    Type? declaringType = baseDefinition.DeclaringType;
                    return declaringType != typeof(FunctionTool) && declaringType != typeof(PrivateFunctionTool);
                }))
            {
                string functionName = method.Name;
                string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                ToolBehaviorAttribute? behavior = method.GetCustomAttribute<ToolBehaviorAttribute>();

                FunctionToolMethodMetadata functionToolMethodMetadata = new FunctionToolMethodMetadata
                {
                    FunctionName = functionName,
                    Description = description,
                    Behavior = behavior,
                    ParameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray(),
                    ReturnType = method.ReturnType,
                    Method = method
                };

                yield return functionToolMethodMetadata;
            }
        }

        private FunctionToolRegistration BuildRegistration(
            object instance,
            FunctionToolMethodMetadata methodMeta,
            AIAgentContext agentContext)
        {
            DtmfKey dtmfKeys = methodMeta.Behavior?.DtmfKeys ?? DtmfKey.None;
            string? dtmfPrompt = methodMeta.Behavior?.DtmfPrompt;
            string? preExecutionPrompt = methodMeta.Behavior?.PreExecutionPrompt;
            string description = methodMeta.Description ?? methodMeta.FunctionName;
            if (dtmfKeys != DtmfKey.None)
            {
                description = $"{description}\n\n{BuildDtmfToolInstruction()}";
            }

            AIFunction aiFunction = AIFunctionFactory.Create(methodMeta.Method, instance, new AIFunctionFactoryOptions
            {
                Name = methodMeta.FunctionName,
                Description = description,
                SerializerOptions = JsonHelper.OPTIONS,
                ConfigureParameterBinding = parameter => parameter.ParameterType == typeof(DtmfInputResult)
                    ? new AIFunctionFactoryOptions.ParameterBindingOptions { ExcludeFromSchema = true }
                    : default,
            });

            if (dtmfKeys != DtmfKey.None)
            {
                // 构建关于按键输入的阻塞逻辑，确保在调用函数前获取按键输入结果
                ParameterInfo dtmfInputParameter = methodMeta.Method
                    .GetParameters()
                    .Single(parameter => parameter.ParameterType == typeof(DtmfInputResult));
                aiFunction = new DtmfGatedAIFunction(
                    aiFunction,
                    agentContext,
                    dtmfKeys,
                    dtmfPrompt!,
                    dtmfInputParameter.Name!);
            }

            FunctionMetadata metadata = aiFunction.ToFunctionMetadata();

            return new FunctionToolRegistration(
                aiFunction,
                metadata,
                methodMeta.Behavior?.DefaultAction ?? ToolAction.Continue,
                dtmfKeys,
                preExecutionPrompt);
        }

        private static string BuildDtmfToolInstruction()
        {
            return DTMF_TOOL_INSTRUCTION;
        }

        private bool IsAllowed(AssistantConfig assistant, Type toolType, FunctionToolMethodMetadata methodMetadata)
        {
            return assistant.AllowedTools.Any(allowed => ToolMatches(allowed, toolType, methodMetadata));
        }

        private static bool ToolMatches(string allowed, Type toolType, FunctionToolMethodMetadata methodMetadata)
        {
            return string.Equals(allowed, methodMetadata.FunctionName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(allowed, toolType.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(allowed, toolType.FullName, StringComparison.OrdinalIgnoreCase);
        }

        #region Validate Allowed Tools
        private bool ValidateDtmfToolDefinitions()
        {
            (Type ToolType, FunctionToolMethodMetadata Method)[] availableTools = this._globalFunctionToolMethodMetadata
                .Concat(this._privateFunctionToolMethodMetadata)
                .SelectMany(entry => entry.Value.Select(method => (entry.Key, method)))
                .ToArray();
            List<string> errors = [];

            foreach ((Type toolType, FunctionToolMethodMetadata method) in availableTools)
            {
                DtmfKey keys = method.Behavior?.DtmfKeys ?? DtmfKey.None;
                ParameterInfo[] inputParameters = method.Method
                    .GetParameters()
                    .Where(parameter => parameter.ParameterType == typeof(DtmfInputResult))
                    .ToArray();
                string toolName = $"{toolType.FullName}.{method.FunctionName}";

                if (keys == DtmfKey.None)
                {
                    if (inputParameters.Length != 0)
                    {
                        errors.Add($"Function tool '{toolName}' declares DtmfInputResult without DtmfKeys.");
                    }
                    if (!string.IsNullOrWhiteSpace(method.Behavior?.DtmfPrompt))
                    {
                        errors.Add($"Function tool '{toolName}' declares DtmfPrompt without DtmfKeys.");
                    }
                    continue;
                }

                if ((keys & ~SUPPORTED_DTMF_KEYS) != DtmfKey.None)
                {
                    errors.Add($"Function tool '{toolName}' contains unsupported DTMF keys.");
                }
                if (inputParameters.Length != 1)
                {
                    errors.Add($"DTMF-gated function tool '{toolName}' must declare exactly one DtmfInputResult parameter.");
                }
                if (string.IsNullOrWhiteSpace(method.Behavior?.DtmfPrompt))
                {
                    errors.Add($"DTMF-gated function tool '{toolName}' must declare a DtmfPrompt.");
                }
            }

            foreach (AssistantConfig assistant in this.Config.AssistantConfigs)
            {
                foreach ((Type toolType, FunctionToolMethodMetadata method) in availableTools)
                {
                    if ((method.Behavior?.DtmfKeys ?? DtmfKey.None) != DtmfKey.None &&
                        this.IsAllowed(assistant, toolType, method) &&
                        !this.UsesFunctionCallIntent(assistant))
                    {
                        errors.Add($"Assistant '{assistant.DialingNumber}' authorizes DTMF-gated tool '{method.FunctionName}' but does not use FunctionCall intent.");
                    }
                }
            }

            foreach (string error in errors)
            {
                this.Logger.LogError("{ValidationError}", error);
            }

            return errors.Count == 0;
        }

        private bool UsesFunctionCallIntent(AssistantConfig assistant)
        {
            return this.Config.ModelConfig.ConfiguredSettings.TryGetValue("Intent", out Dictionary<string, Dictionary<string, string>>? intentSettings) &&
                intentSettings.TryGetValue(assistant.Intent, out Dictionary<string, string>? intentSetting) &&
                intentSetting.TryGetValue("Type", out string? intentType) &&
                string.Equals(intentType, "FunctionCall", StringComparison.OrdinalIgnoreCase);
        }

        private bool ValidateAllowedTools()
        {
            (Type Type, FunctionToolMethodMetadata Method)[] availableTools =
                this._globalFunctionToolMethodMetadata
                    .Concat(this._privateFunctionToolMethodMetadata)
                    .SelectMany(entry => entry.Value.Select(method => (entry.Key, method)))
                    .ToArray();

            if (!availableTools.Any())
            {
                this.Logger.LogWarning("没有注册任何 FunctionTool，无法验证允许的工具。");
                return true;
            }

            IEnumerable<string> availableNames = availableTools.SelectMany(
                tool => new[]
                {
                    tool.Method.FunctionName,
                    tool.Type.Name,
                    tool.Type.FullName ?? string.Empty
                });
            IReadOnlyList<string> errors = ValidateAllowedTools(
                this.Config.AssistantConfigs,
                availableNames);
            foreach (string error in errors)
            {
                this.Logger.LogError("{ValidationError}", error);
            }

            return errors.Count == 0;
        }

        private static IReadOnlyList<string> ValidateAllowedTools(IEnumerable<AssistantConfig> assistants, IEnumerable<string> availableTools)
        {
            ArgumentNullException.ThrowIfNull(assistants);
            ArgumentNullException.ThrowIfNull(availableTools);

            var available = availableTools
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (AssistantConfig? assistant in assistants)
            {
                if (assistant is null)
                {
                    continue;
                }

                foreach (string? allowedTool in assistant.AllowedTools ?? [])
                {
                    if (string.IsNullOrWhiteSpace(allowedTool) || !available.Contains(allowedTool))
                    {
                        errors.Add($"Assistant '{assistant.DialingNumber}' references unknown tool '{allowedTool}'.");
                    }
                }
            }

            return errors;
        }
        #endregion

        private ServerInfoAdapter CreateServerInfoAdapter()
        {
            return new ServerInfoAdapter(GlobalVariables.ServerName, this.Config);
        }

        #region Release Tools
        private static async Task ReleasePrivateToolsAsync(IEnumerable<PrivateFunctionTool> tools)
        {
            List<Exception> errors = [];
            foreach (PrivateFunctionTool tool in tools.Distinct())
            {
                try
                {
                    await tool.OnDeviceClosedAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }

                try
                {
                    await tool.OnFunctionToolReleasedAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }

                if (tool is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }

            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }

        private void ReleaseGlobalTools()
        {
            foreach (FunctionTool instance in this._globalFunctionTools)
            {
                try
                {
                    instance.OnFunctionToolReleasedAsync()
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    this.Logger.LogError(exception, "释放全局 FunctionTool {ToolType} 失败。", instance.GetType().FullName);
                }
                finally
                {
                    if (instance is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
            this._globalFunctionTools.Clear();
        }
        #endregion

        public override void Dispose()
        {
            this.ReleaseGlobalTools();
        }

        private sealed class PrivateFunctionToolLifetime : IDisposable
        {
            private IReadOnlyList<PrivateFunctionTool>? _tools;
            private readonly ILogger _logger;

            public PrivateFunctionToolLifetime(IReadOnlyList<PrivateFunctionTool> tools, ILogger logger)
            {
                this._tools = tools;
                this._logger = logger;
            }

            public void Dispose()
            {
                IReadOnlyList<PrivateFunctionTool>? tools =
                    Interlocked.Exchange(ref this._tools, null);
                if (tools is null)
                {
                    return;
                }

                try
                {
                    FunctionToolManager.ReleasePrivateToolsAsync(tools).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    this._logger.LogError(
                        exception,
                        "释放通话级 FunctionTool 失败。");
                }
            }
        }
    }
}
