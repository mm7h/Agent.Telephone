using Agent.Telephone.Abstractions.FunctionTools;
using SIPSorcery.SIP;

namespace Agent.Telephone.Providers.CallControl
{
    internal static class CallTransferPolicy
    {
        public static CallTransferResult? ValidateTarget(
            string? targetNumber,
            string? callerNumber)
        {
            if (string.IsNullOrWhiteSpace(targetNumber))
            {
                return new CallTransferResult(
                    CallTransferStatus.InvalidTarget,
                    targetNumber ?? string.Empty,
                    "目标号码不能为空。");
            }

            string normalized = targetNumber.Trim();
            return string.Equals(normalized, callerNumber, StringComparison.OrdinalIgnoreCase)
                ? new CallTransferResult(
                    CallTransferStatus.SelfTransfer,
                    normalized,
                    "不能把通话转接给当前主叫。")
                : null;
        }

        public static CallTransferResult? FromEndpointStatus(
            string targetNumber,
            RegisteredEndpointResolution resolution)
        {
            return resolution.Status switch
            {
                RegisteredEndpointStatus.Busy => new CallTransferResult(
                    CallTransferStatus.Busy,
                    targetNumber,
                    "目标设备正在通话。"),
                RegisteredEndpointStatus.Offline => new CallTransferResult(
                    CallTransferStatus.Offline,
                    targetNumber,
                    "目标设备未注册或注册已过期。"),
                RegisteredEndpointStatus.Available when resolution.Lease is null =>
                    new CallTransferResult(
                        CallTransferStatus.Offline,
                        targetNumber,
                        "目标设备未注册或注册已过期。"),
                _ => null,
            };
        }

        public static CallTransferResult MapFailure(
            string targetNumber,
            SIPResponseStatusCodesEnum? responseStatus,
            string? failureReason,
            bool callEnded)
        {
            CallTransferStatus status = callEnded
                ? CallTransferStatus.CallEnded
                : responseStatus switch
                {
                    SIPResponseStatusCodesEnum.BusyHere
                        or SIPResponseStatusCodesEnum.BusyEverywhere => CallTransferStatus.Busy,
                    SIPResponseStatusCodesEnum.RequestTimeout => CallTransferStatus.TimedOut,
                    SIPResponseStatusCodesEnum.NotAcceptableHere
                        or SIPResponseStatusCodesEnum.UnsupportedMediaType => CallTransferStatus.CodecNotSupported,
                    SIPResponseStatusCodesEnum.Decline
                        or SIPResponseStatusCodesEnum.Forbidden
                        or SIPResponseStatusCodesEnum.NotFound => CallTransferStatus.Rejected,
                    _ when failureReason?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true
                        => CallTransferStatus.TimedOut,
                    _ => CallTransferStatus.Failed,
                };

            return new CallTransferResult(status, targetNumber, failureReason);
        }
    }
}
