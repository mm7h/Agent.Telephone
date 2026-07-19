using Microsoft.Extensions.Hosting;
using Agent.Telephone.Abstractions.Configs;

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
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        IServerBuilder Initialize(TelephoneConfig config);
        /// <summary>
        /// 添加自定义验证
        /// </summary>
        /// <returns></returns>
        IServerBuilder WithVerify<T>() where T : class, IBasicVerify;
        /// <summary>
        /// 构建服务引擎
        /// </summary>
        /// <returns></returns>
        IHost Build();
    }
}
