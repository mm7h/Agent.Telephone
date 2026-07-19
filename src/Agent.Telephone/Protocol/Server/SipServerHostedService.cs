using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Management;
using Agent.Telephone.Protocol.Server.Middlewares;
using DnsClient.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Telephone.Protocol.Server
{
    internal class SipServerHostedService : IHostedService
    {
        private readonly SIPConfig _sipConfig;
        private readonly SIPTransport _sipTransport;
        private readonly DeviceContainerMiddleware _deviceContainerMiddleware;
        private readonly ILogger<SipServerHostedService> _logger;

        public SipServerHostedService(SIPConfig sipConfig, SIPTransport sipTransport, DeviceContainerMiddleware deviceContainerMiddleware, ILogger<SipServerHostedService> logger)
        {
            this._sipConfig = sipConfig;
            this._sipTransport = sipTransport;
            this._deviceContainerMiddleware = deviceContainerMiddleware;
            this._logger = logger;
        }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            SIPChannel channel = this._sipTransport.CreateChannel(SIPProtocolsEnum.udp, AddressFamily.InterNetwork, this._sipConfig.Port);
            this._sipTransport.AddSIPChannel(channel);

            this._deviceContainerMiddleware.SubscribeSIPTransportEvents(this._sipTransport);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            this._deviceContainerMiddleware.UnsubscribeSIPTransportEvents(this._sipTransport);

            this._sipTransport.Shutdown();
            return Task.CompletedTask;
        }
    }
}
