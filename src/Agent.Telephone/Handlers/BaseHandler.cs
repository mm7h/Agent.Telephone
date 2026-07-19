using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Handlers
{
    internal abstract class BaseHandler : IHandler
    {
        public BaseHandler(IServiceProvider serviceProvider, ILogger logger)
        {
            this.ServiceProvider = serviceProvider;
            this.Logger = logger;
        }

        public IServiceProvider ServiceProvider { get; }
        public ILogger Logger { get; }
        public abstract string HandlerName { get; }

        public abstract bool Build(DeviceContext deviceContext);

        public virtual void Dispose()
        {
        }
    }
}
