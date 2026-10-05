using SIPSorcery.SIP;

namespace Agent.Telephone.Helpers
{
    internal static class SIPHelper
    {
        private const int DEFAULT_REGISTRATION_EXPIRES_SECONDS = 3600;

        public static string GetDeviceId(this SIPRequest sipRequest)
        {
            SIPURI aor = sipRequest.GetCallerAor();
            var user = Uri.EscapeDataString(aor.User);
            var host = Uri.EscapeDataString(aor.Host.ToLowerInvariant());

            return $"sip-device:v1:default:{user}@{host}";
        }

        public static SIPURI GetCallerAor(this SIPRequest sipRequest)
        {
            SIPURI? aor = sipRequest.Header.From?.FromURI;
            if (aor is null ||
                string.IsNullOrWhiteSpace(aor.User) ||
                string.IsNullOrWhiteSpace(aor.Host))
            {
                throw new InvalidOperationException("SIP 请求缺少有效的 From AOR。");
            }

            return aor;
        }

        public static string GetAssistantNumber(this SIPRequest sipRequest)
        {
            string? dialingNumber = sipRequest.URI?.User;
            if (string.IsNullOrWhiteSpace(dialingNumber))
            {
                throw new InvalidOperationException("INVITE 缺少有效的 Request-URI 用户号码。");
            }

            return dialingNumber;
        }

        public static (SIPURI Contact, int ExpiresSeconds) GetRegistration(this SIPRequest sipRequest)
        {
            if (sipRequest.Header.Contact is null || sipRequest.Header.Contact.Count != 1)
            {
                throw new InvalidOperationException("REGISTER 必须包含一个 Contact。");
            }

            SIPContactHeader contactHeader = sipRequest.Header.Contact[0];
            SIPURI contact = contactHeader.ContactURI;
            long parsedExpires = contactHeader.Expires >= 0
                ? contactHeader.Expires
                : sipRequest.Header.Expires >= 0
                    ? sipRequest.Header.Expires
                    : DEFAULT_REGISTRATION_EXPIRES_SECONDS;
            if (parsedExpires > int.MaxValue)
            {
                throw new InvalidOperationException("REGISTER Expires 超出支持范围。");
            }

            int expires = (int)parsedExpires;

            bool isWildcard = string.Equals(contact.ToString(), "*", StringComparison.Ordinal);
            if (isWildcard && expires != 0)
            {
                throw new InvalidOperationException("通配 Contact 只能用于 Expires=0 的注销请求。");
            }

            if (!isWildcard &&
                (string.IsNullOrWhiteSpace(contact.User) || string.IsNullOrWhiteSpace(contact.Host)))
            {
                throw new InvalidOperationException("REGISTER Contact 不是有效的 SIP URI。");
            }

            return (contact, expires);
        }
    }
}
