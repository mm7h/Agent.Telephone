using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Handlers
{
    internal interface IHandler : IDisposable
    {
        string HandlerName { get; }
        bool Build();
        DeviceContext DeviceContext { get; set; }
    }
}
