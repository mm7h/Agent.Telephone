using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.Common.Contexts
{
    /// <summary>
    /// Contains the result of an assistant switch request.
    /// </summary>
    /// <param name="Status">The assistant switch status.</param>
    /// <param name="TargetAssistantNumber">The requested assistant dialing number.</param>
    /// <param name="Message">An optional message that describes the result.</param>
    public sealed record AssistantSwitchResult(AssistantSwitchStatus Status, string TargetAssistantNumber, string? Message = null)
    {
        /// <summary>
        /// Gets a value indicating whether the switch request was accepted.
        /// </summary>
        public bool Succeeded => this.Status == AssistantSwitchStatus.Accepted;
    }
}
