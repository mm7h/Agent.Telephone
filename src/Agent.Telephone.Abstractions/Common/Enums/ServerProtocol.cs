using System.ComponentModel;

namespace Agent.Telephone.Abstractions.Common.Enums
{
    public enum ServerProtocol
    {
        [Description("WebSocket")]
        WebSocket,
        [Description("Mqtt")]
        Mqtt,
    }
}
