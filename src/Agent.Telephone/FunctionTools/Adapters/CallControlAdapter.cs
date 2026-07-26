using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.CallControl;

namespace Agent.Telephone.FunctionTools.Adapters
{
    /// <summary>
    /// FunctionTool-facing call-control adapter. It deliberately exposes no
    /// SIPSorcery object; SIP signalling is handled by SIPCallControlHandler.
    /// </summary>
    internal sealed class CallControlAdapter : ICallControl
    {
        private readonly ActiveCallContext _call;
        private readonly CallControlProvider _callControlProvider;

        public CallControlAdapter(
            ActiveCallContext call,
            CallControlProvider callControlProvider)
        {
            this._call = call;
            this._callControlProvider = callControlProvider;
        }

        public string? CallerNumber => this._call.CallerNumber;

        public string? AssistantNumber => this._call.DialedNumber;

        public bool IsCallActive => this._call.UserAgent.IsCallActive;

        public Task<CallTransferResult> TransferAsync(
            string targetNumber,
            CancellationToken cancellationToken = default)
        {
            return this._callControlProvider.TransferAsync(
                this._call,
                targetNumber,
                cancellationToken);
        }
    }
}
