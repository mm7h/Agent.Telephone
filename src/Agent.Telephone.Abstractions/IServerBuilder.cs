using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Abstractions.Persistence;
using Microsoft.Extensions.Hosting;

namespace Agent.Telephone.Abstractions
{
    public interface IServerBuilder
    {
        /// <summary>
        /// 获取当前的HostBuilder
        /// </summary>
        IHostBuilder HostBuilder { get; }
        /// <summary>
        /// 初始化服务
        /// </summary>
        /// <param name="config">配置信息</param>
        /// <param name="telephoneStore">电话系统持久化存储实现</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        IServerBuilder Initialize(TelephoneConfig config, ITelephoneStore telephoneStore);
        /// <summary>
        /// 添加自定义验证
        /// </summary>
        /// <returns></returns>
        IServerBuilder WithVerify<T>() where T : class, IBasicVerify;
        /// <summary>
        /// 注册自定义函数工具。
        /// </summary>
        /// <typeparam name="TFunctionTool">需要注册的函数工具类型。</typeparam>
        /// <returns></returns>
        IServerBuilder WithFunctionTools<TFunctionTool>() where TFunctionTool : class, IFunctionTool, new();
        /// <summary>
        /// 注册自定义函数工具。
        /// </summary>
        /// <typeparam name="TFunctionTool">需要注册的函数工具类型。</typeparam>
        /// <returns></returns>
        IServerBuilder WithPrivateFunctionTools<TFunctionTool>() where TFunctionTool : class, IPrivateFunctionTool, new();
        /// <summary>
        /// 构建服务引擎
        /// </summary>
        /// <returns></returns>
        IHost Build();
    }
}
