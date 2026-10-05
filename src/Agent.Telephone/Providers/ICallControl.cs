using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers
{
    /// <summary>
    /// Call-control provider contract. Implementations (for example
    /// <see cref="Agent.Telephone.Providers.CallControl.AssistantRoleControl"/>)
    /// share the same capability and differ only in internal logic, so consumers
    /// depend on this abstraction instead of a concrete provider.
    /// </summary>
    internal interface ICallControl : IProvider<List<AssistantConfig>>
    {
        /// <summary>
        /// Switches the active call to the assistant for the target number
        /// without ending the SIP dialogue or RTP session.
        /// </summary>
        /// <param name="call">The active call whose Agent session is replaced.</param>
        /// <param name="targetAssistantNumber">The dialing number of the target assistant.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The result of the assistant switch request.</returns>
        Task<AssistantSwitchResult> SwitchAssistantAsync(
            ActiveCallContext call,
            string targetAssistantNumber,
            CancellationToken cancellationToken);
    }
}
