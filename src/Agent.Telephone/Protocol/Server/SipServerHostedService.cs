using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Management;
using Agent.Telephone.Protocol.Server.Middlewares;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using System.Net;

namespace Agent.Telephone.Protocol.Server
{
    internal class SipServerHostedService : IHostedService
    {
        private readonly SIPConfig _sipConfig;
        private readonly SIPTransport _sipTransport;
        private readonly DeviceContainerMiddleware _deviceContainerMiddleware;
        private readonly DeviceContextManager _deviceManager;
        private readonly ILogger<SipServerHostedService> _logger;

        public SipServerHostedService(
            SIPConfig sipConfig,
            SIPTransport sipTransport,
            DeviceContainerMiddleware deviceContainerMiddleware,
            DeviceContextManager deviceManager,
            ILogger<SipServerHostedService> logger)
        {
            this._sipConfig = sipConfig;
            this._sipTransport = sipTransport;
            this._deviceContainerMiddleware = deviceContainerMiddleware;
            this._deviceManager = deviceManager;
            this._logger = logger;
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this._logger.LogInformation("正在启动 SIP 服务");
            if (!IPAddress.TryParse(this._sipConfig.IP, out IPAddress? bindAddress) ||
                bindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException($"SIPConfig.IP 不是有效的 IPv4 地址：{this._sipConfig.IP}");
            }

            await this._deviceManager.RestoreRegistrationsAsync(cancellationToken);

            SIPChannel channel = new SIPUDPChannel(bindAddress, this._sipConfig.Port);
            this._sipTransport.AddSIPChannel(channel);

            this._deviceContainerMiddleware.SubscribeSIPTransportEvents(this._sipTransport);

            this._logger.LogInformation("已启动 SIP 服务，监听地址：{IP}:{Port}", this._sipConfig.IP, this._sipConfig.Port);

        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            this._logger.LogInformation("正在停止 SIP 服务...");

            this._deviceContainerMiddleware.UnsubscribeSIPTransportEvents(this._sipTransport);

            this._sipTransport.Shutdown();

            this._logger.LogInformation("已停止 SIP 服务");

            return Task.CompletedTask;
        }
    }
}
