using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Abstractions.Store;
using Agent.Telephone.Management;
using Agent.Telephone.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent.Telephone
{
    internal class ServerBuilder : IServerBuilder
    {
        private static readonly Lazy<IServerBuilder> s_lazyInstance = new Lazy<IServerBuilder>(() => new ServerBuilder());

        private ServerBuilder()
        {
            this.HostBuilder = Host.CreateDefaultBuilder();
        }
        internal ServerBuilder(IHostBuilder hostBuilder)
        {
            this.HostBuilder = hostBuilder;
        }

        public static IServerBuilder CreateServerBuilder() => s_lazyInstance.Value;
        public static IServerBuilder CreateServerBuilder(IHostBuilder hostBuilder) => new ServerBuilder(hostBuilder);

        public IHostBuilder HostBuilder { get; private set; }

        public IServerBuilder Initialize(TelephoneConfig config, IMessageStore messageStore)
        {
            return this.Initialize(config, DefaultMemoryStore.Default, messageStore);
        }

        private IServerBuilder Initialize(TelephoneConfig config, IStore connectionStore, IMessageStore messageStore)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config), "TelephoneConfig cannot be null.");
            }
            ArgumentNullException.ThrowIfNull(messageStore);

            this.HostBuilder = this.HostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton(config);
                services.AddSingleton(connectionStore);
                services.AddSingleton<IMessageStore>(messageStore);

            })
            .RegisterLogger(config)
            .RegisterResources(config)
            .RegisterDevices()
            .RegisterProviders(config)
            .RegisterHandlers()
            .RegisterFunctionTools()
            .RegisterObjectPools()
            .RegisterProtocol(config);

#if DEBUG
            this.HostBuilder.UseEnvironment("Development");
#else
            this.HostBuilder.UseEnvironment("Production");
#endif

            return this;
        }

        IServerBuilder IServerBuilder.WithVerify<T>()
        {
            this.HostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<IBasicVerify, T>();
            });

            return this;
        }

        public IServerBuilder WithFunctionTools<TFunctionTool>() where TFunctionTool : class, IFunctionTool, new()
        {
            this.HostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFunctionTool, TFunctionTool>();
            });

            return this;
        }

        public IServerBuilder WithPrivateFunctionTools<TFunctionTool>() where TFunctionTool : class, IPrivateFunctionTool, new()
        {
            this.HostBuilder.ConfigureServices((context, services) =>
            {
                services.AddTransient<IPrivateFunctionTool, TFunctionTool>();
            });

            return this;
        }

        public IHost Build()
        {
            IHost host = this.HostBuilder.Build();
            this.BuildComponents(host.Services);
            return host;
        }

        private void BuildComponents(IServiceProvider serviceProvider)
        {
            ResourceManager resourceManager = serviceProvider.GetRequiredService<ResourceManager>();
            ProviderManager providerManager = serviceProvider.GetRequiredService<ProviderManager>();
            FunctionToolManager functionToolManager = serviceProvider.GetRequiredService<FunctionToolManager>();

            bool loaded = resourceManager.BuildComponent();
            if (!loaded)
            {
                Serilog.Log.CloseAndFlush();
                throw new ApplicationException("加载资源组件失败。请检查配置和资源实现。");
            }
            bool builded = providerManager.BuildComponent();
            if (!builded)
            {
                Serilog.Log.CloseAndFlush();
                throw new ApplicationException("加载提供者组件失败。请检查配置和提供者实现。");
            }
            bool toolsLoaded = functionToolManager.BuildComponent();
            if (!toolsLoaded)
            {
                Serilog.Log.CloseAndFlush();
                throw new ApplicationException("加载自定义 function 组件失败。请检查配置和提供者实现。");
            }
        }
    }
}
