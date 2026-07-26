using SIPSorcery.SIP;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed record RegistrationBinding(
        SIPURI Aor,
        SIPURI Contact,
        SIPEndPoint LocalEndPoint,
        SIPEndPoint RemoteEndPoint,
        DateTimeOffset RegisteredAt,
        DateTimeOffset RefreshedAt,
        DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= this.ExpiresAt;
    }
}
