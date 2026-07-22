using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Helpers;
using SIPSorcery.SIP;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class DeviceContext : IDisposable
    {
        private readonly SIPTransport _sipTransport;

        public DeviceContext(SIPTransport sipTransport, SIPRequest sipRequest, List<AssistantConfig> availableAssistants)
        {
            this._sipTransport = sipTransport;

            this.DeviceId = sipRequest.GetDeviceId();
            this.Contact = sipRequest.URI;
            this.AvailableAssistants = availableAssistants.ToDictionary(i => i.DialingNumber).AsReadOnly();
            this.RemoteEndPoint = sipRequest.RemoteSIPEndPoint;
            this.AudioInPacket = new AudioInPacket();
            this.AudioOutputPacket = new AudioOutputPacket();
        }

        public string DeviceId { get; }
        public SIPURI Contact { get; set; }
        public SIPEndPoint RemoteEndPoint { get; set; }
        public AudioInPacket AudioInPacket { get; }
        public AudioOutputPacket AudioOutputPacket { get; }
        public ActiveCallContext? ActiveCall { get; private set; }
        public IReadOnlyDictionary<string, AssistantConfig> AvailableAssistants { get; }

        public void InitializeCallSession(SIPRequest sipRequest)
        {
            this.CloseCallSession();
            this.ActiveCall = new ActiveCallContext(this._sipTransport, sipRequest, this);
        }

        public void CloseCallSession()
        {
            this.ActiveCall?.Dispose();
            this.ActiveCall = null;
        }

        public void Dispose() => this.CloseCallSession();
    }
}
