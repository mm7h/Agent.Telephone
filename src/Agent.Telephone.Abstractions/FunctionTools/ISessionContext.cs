using System.Net;

namespace Agent.Telephone.Abstractions.FunctionTools
{
    public interface IDeviceContext
    {
        string DeviceId { get; }
        DateTimeOffset LoginTime { get; }
        DateTimeOffset LastActiveTime { get; }
        IPEndPoint LocalEndPoint { get; }
        IPEndPoint RemoteEndPoint { get; }
    }
}
