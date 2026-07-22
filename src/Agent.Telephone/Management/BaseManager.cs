using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using System.Text.RegularExpressions;

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
        public static string ConvertToKebabCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            return Regex.Replace(input, "(?<!^)([A-Z])", "-$1").ToLower();
        }
        public virtual Task OnSIPDeviceRegisteringAsync(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            return Task.CompletedTask;
        }
        public virtual Task<bool> OnSIPDeviceRegisteredAsync(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            return Task.FromResult(true);
        }
        public virtual Task OnSIPDeviceUnregisterAsync(DeviceContext deviceContext, SIPTransport sipTransport, SIPRequest sipRequest)
        {
            return Task.CompletedTask;
        }

        public virtual void Dispose()
        { }
    }
}
