using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Codex.AppServer;
using Agent.Telephone.Codex.Abstractions.Common.Configs;
using Agent.Telephone.Codex.Abstractions.Functions;
using Agent.Telephone.Codex.FunctionTools;
using Agent.Telephone.Codex.Persistence;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130 // 保持 WithCodexAssistant 扩展方法的公开命名空间。
namespace Agent.Telephone
{
    /// <summary>
    /// 注册 Codex 电话助手。
    /// </summary>
    public static class CodexExtension
    {
        /// <summary>
        /// 通过 Builder Action 注册 Codex 电话助手。
        /// </summary>
        /// <param name="builder">当前服务构建器。</param>
        /// <param name="configure">Codex 运行配置。</param>
        /// <returns>当前服务构建器。</returns>
        public static IServerBuilder WithCodexAssistant(this IServerBuilder builder, Action<CodexAssistantOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            CodexAssistantOptions options = new();
            configure(options);
            options.Validate();

            builder.HostBuilder.ConfigureServices((_, services) =>
            {
                services.AddSingleton(options);
                services.AddSingleton<CodexThreadStore>();
                services.AddSingleton<ICodexFunction, CodexAppServerFunction>();
                services.AddTransient<IPrivateFunctionTool, CodexAssistant>();
            });

            return builder;
        }
    }
}
#pragma warning restore IDE0130
