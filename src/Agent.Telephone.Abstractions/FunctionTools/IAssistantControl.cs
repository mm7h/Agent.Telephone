using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.FunctionTools
{
    /// <summary>
    /// Exposes call control to private function tools without leaking SIP library types.
    /// </summary>
    public interface IAssistantControl
    {
        string? CallerNumber { get; }
        string? AssistantNumber { get; }
        bool IsCallActive { get; }

        /// <summary>
        /// Switches the current call to the agent for the selected number
        /// without ending the call.
        /// </summary>
        /// <param name="targetAssistantNumber">The dialing number of the assistant to activate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The result of accepting the assistant switch request.</returns>
        Task<AssistantSwitchResult> SwitchAssistantAsync(string targetAssistantNumber, CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts a single-key DTMF selection window for the active call.
        /// </summary>
        Task<DtmfInputResult> RequestDtmfInputAsync(
            DtmfKey keys,
            CancellationToken cancellationToken = default);
    }
}
