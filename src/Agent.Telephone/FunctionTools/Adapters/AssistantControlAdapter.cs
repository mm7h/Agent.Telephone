using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;

namespace Agent.Telephone.FunctionTools.Adapters
{
    /// <summary>
    /// FunctionTool-facing call-control adapter. It deliberately exposes no
    /// SIPSorcery object and delegates to the call-control provider through its
    /// abstract interface.
    /// </summary>
    internal sealed class AssistantControlAdapter : IAssistantControl
    {
        private readonly ActiveCallContext _activeCall;

        /// <summary>
        /// The call-control provider, exposed through its abstract interface.
        /// </summary>
        public ICallControl CallControl { get; }

        public AssistantControlAdapter(ActiveCallContext activeCall, ICallControl callControl)
        {
            this._activeCall = activeCall;
            this.CallControl = callControl;
        }

        public string? CallerNumber => this._activeCall.CallerNumber;

        public string? AssistantNumber => this._activeCall.DialedNumber;

        public bool IsCallActive => this._activeCall.UserAgent.IsCallActive;

        /// <inheritdoc />
        public Task<AssistantSwitchResult> SwitchAssistantAsync(
            string targetAssistantNumber,
            CancellationToken cancellationToken = default)
        {
            return this.CallControl.SwitchAssistantAsync(this._activeCall, targetAssistantNumber, cancellationToken);
        }
    }
}
