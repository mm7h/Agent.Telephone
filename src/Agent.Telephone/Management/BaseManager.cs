using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Management
{
    internal abstract class BaseManager : IDisposable
    {
        protected BaseManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger logger)
        {
            this.ServiceProvider = serviceProvider;
            this.Logger = logger;
            this.Config = config;
        }
        public IServiceProvider ServiceProvider { get; }
        public ILogger Logger { get; }
        public TelephoneConfig Config { get; }

        public abstract bool BuildComponent();

        public virtual void OnSIPDeviceRegistering(SIPTransport sipTransport, SIPRequest sipRequest) { }
        public virtual void OnSIPDeviceRegistered(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest) { }
        public virtual void OnSIPDeviceUnregister(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest) { }

        public virtual void Dispose()
        { }
    }
}
