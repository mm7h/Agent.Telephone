using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Handlers
{
    internal interface IHandler : IDisposable
    {
        string HandlerName { get; }
        bool Build(DeviceContext deviceContext);
    }
}
