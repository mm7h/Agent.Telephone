using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Abstractions.Common.Enums;
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

        public AssistantControlAdapter(ActiveCallContext activeCall)
        {
            this._activeCall = activeCall;
        }

        public string? CallerNumber => this._activeCall.CallerNumber;

        public string? AssistantNumber => this._activeCall.DialedNumber;

        public bool IsCallActive => this._activeCall.UserAgent.IsCallActive;

        /// <inheritdoc />
        public Task<AssistantSwitchResult> SwitchAssistantAsync(
            string targetAssistantNumber,
            CancellationToken cancellationToken = default)
        {
            ICallControl? callControl = this._activeCall.AIAgentContext.PrivateProvider.CallControl;
            return callControl is null
                ? Task.FromResult(new AssistantSwitchResult(
                    AssistantSwitchStatus.Failed,
                    targetAssistantNumber,
                    "通话控制功能尚未就绪。"))
                : callControl.SwitchAssistantAsync(this._activeCall, targetAssistantNumber, cancellationToken);
        }

        public Task<DtmfInputResult> RequestDtmfInputAsync(
            DtmfKey keys,
            CancellationToken cancellationToken = default)
        {
            IDtmfInput? dtmfInput = this._activeCall.AIAgentContext.PrivateProvider.DtmfInput;
            return dtmfInput is null
                ? Task.FromResult(new DtmfInputResult(
                    DtmfInputStatus.Unavailable,
                    "按键功能尚未就绪。"))
                : dtmfInput.RequestDtmfInputAsync(this._activeCall, keys, cancellationToken);
        }
    }
}
