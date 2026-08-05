using System.ComponentModel;
using System.Reflection;
using Agent.Telephone.Abstractions.Common.Attributes;
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
                services.AddTransient<IPrivateFunctionTool, AssistantSwitchFunctionTool>();
                services.AddTransient<IPrivateFunctionTool, DtmfInputFunctionTool>();
            });
        }

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
                if (!this.ValidateAllowedTools())
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

        public override Task<bool> OnSIPDeviceRegisteredAsync(
            DeviceContext deviceContext,
            SIPTransport sipTransport,
            SIPRequest sipRequest)
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
            foreach (FunctionTool instance in this._globalFunctionTools)
            {
                if (!this._globalFunctionToolMethodMetadata.TryGetValue(instance.GetType(), out IEnumerable<FunctionToolMethodMetadata>? methodMetas))
                {
                    continue;
                }

                foreach (FunctionToolMethodMetadata methodMeta in methodMetas.Where(method => this.IsAllowed(activeCall.AssistantConfig, instance.GetType(), method)))
                {
                    FunctionToolRegistration registration = this.BuildRegistration(instance, methodMeta);
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
                    instance.CallControl = new AssistantControlAdapter(activeCall);

                    initializedTools.Add(instance);
                    await instance.OnFunctionToolInitializedAsync();
                    activeCall.CallToken.ThrowIfCancellationRequested();
                    await instance.OnDeviceConnectedAsync();
                    activeCall.CallToken.ThrowIfCancellationRequested();

                    foreach (FunctionToolMethodMetadata methodMeta in allowedMethods)
                    {
                        FunctionToolRegistration registration = this.BuildRegistration(instance, methodMeta);
                        activeCall.AIAgentContext.PrivateProvider.AddFunctionToolRegistration(instance, registration);
                    }
                }

                activeCall.AIAgentContext.RegisterOwnedResource(
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
                        await FunctionToolManager.ReleasePrivateToolsAsync(initializedTools)
                            ;
                    }
                    catch (Exception releaseException)
                    {
                        this.Logger.LogError(
                            releaseException,
                            "回滚通话级 FunctionTool 初始化时失败。");
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
                            this.Logger.LogError(
                                disposeException,
                                "释放未使用的通话级 FunctionTool {ToolType} 时失败。",
                                tool.GetType().FullName);
                        }
                    }
                }
            }
        }

        public Task<bool> BuildForActiveCallAsync(
            DeviceContext deviceContext,
            SIPTransport sipTransport)
        {
            return this.BuildForActiveCallAsync(deviceContext);
        }



        public override void Dispose()
        {
            this.ReleaseGlobalTools();
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
                    this.Logger.LogError(
                        exception,
                        "释放全局 FunctionTool {ToolType} 失败。",
                        instance.GetType().FullName);
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
                AIFunction aiFunction = AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
                {
                    Name = functionName,
                    Description = description ?? functionName,
                    SerializerOptions = JsonHelper.OPTIONS
                });

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

        private FunctionToolRegistration BuildRegistration(object instance, FunctionToolMethodMetadata methodMeta)
        {
            DtmfKey dtmfKeys = methodMeta.Behavior?.DtmfKeys ?? DtmfKey.None;
            string description = methodMeta.Description ?? methodMeta.FunctionName;
            if (dtmfKeys != DtmfKey.None)
            {
                description = $"{description}。可由电话按键 {dtmfKeys} 选择。";
            }

            AIFunction aiFunction = AIFunctionFactory.Create(methodMeta.Method, instance, new AIFunctionFactoryOptions
            {
                Name = methodMeta.FunctionName,
                Description = description,
                SerializerOptions = JsonHelper.OPTIONS
            });

            FunctionMetadata metadata = aiFunction.ToFunctionMetadata();

            return new FunctionToolRegistration(
                aiFunction,
                metadata,
                methodMeta.Behavior?.DefaultAction ?? ToolAction.Continue,
                dtmfKeys);
        }

        private bool IsAllowed(
            AssistantConfig assistant,
            Type toolType,
            FunctionToolMethodMetadata methodMetadata)
        {
            return assistant.AllowedTools?.Any(
                allowed => ToolMatches(allowed, toolType, methodMetadata)) == true;
        }

        private bool ValidateAllowedTools()
        {
            (Type Type, FunctionToolMethodMetadata Method)[] availableTools =
                this._globalFunctionToolMethodMetadata
                    .Concat(this._privateFunctionToolMethodMetadata)
                    .SelectMany(
                        entry => entry.Value.Select(method => (entry.Key, method)))
                    .ToArray();
            IEnumerable<string> availableNames = availableTools.SelectMany(
                tool => new[]
                {
                    tool.Method.FunctionName,
                    tool.Type.Name,
                    tool.Type.FullName ?? string.Empty
                });
            IReadOnlyList<string> errors = TelephoneConfigValidator.ValidateAllowedTools(
                this.Config.AssistantConfigs,
                availableNames);
            foreach (string error in errors)
            {
                this.Logger.LogError("{ValidationError}", error);
            }

            return errors.Count == 0;
        }

        private static bool ToolMatches(
            string allowed,
            Type toolType,
            FunctionToolMethodMetadata methodMetadata)
        {
            return string.Equals(allowed, methodMetadata.FunctionName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(allowed, toolType.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(allowed, toolType.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private ServerInfoAdapter CreateServerInfoAdapter()
        {
            return new ServerInfoAdapter(GlobalVariables.ServerName, this.Config);
        }

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

        private sealed class PrivateFunctionToolLifetime : IDisposable
        {
            private IReadOnlyList<PrivateFunctionTool>? _tools;
            private readonly ILogger _logger;

            public PrivateFunctionToolLifetime(
                IReadOnlyList<PrivateFunctionTool> tools,
                ILogger logger)
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
