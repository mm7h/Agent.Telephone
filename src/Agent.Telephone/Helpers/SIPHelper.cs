using SIPSorcery.SIP;

namespace Agent.Telephone.Helpers
{
    internal static class SIPHelper
    {
        public static string GetDeviceId(this SIPRequest sipRequest)
        {
            SIPURI aor = sipRequest.Header.To.ToURI;

            if (string.IsNullOrWhiteSpace(aor.User) ||
                string.IsNullOrWhiteSpace(aor.Host))
            {
                throw new InvalidOperationException("REGISTER 缺少有效的 SIP AOR。");
            }

            var user = Uri.EscapeDataString(aor.User);
            var host = Uri.EscapeDataString(aor.Host.ToLowerInvariant());

            return $"sip-device:v1:default:{user}@{host}";
        }
    }
}
