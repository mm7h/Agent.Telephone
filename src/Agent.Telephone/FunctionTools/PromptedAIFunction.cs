using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.FunctionTools
{
    internal sealed class PromptedAIFunction : DelegatingAIFunction
    {
        private readonly string _deviceId;
        private readonly ActiveCallContext? _activeCall;

        public PromptedAIFunction(AIFunction innerFunction, string deviceId, ActiveCallContext? activeCall) : base(innerFunction)
        {
            this._deviceId = deviceId;
            this._activeCall = activeCall;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await ToolExecutionPromptDispatcher.DispatchAsync(this._deviceId, this.Name, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            bool resumeUserAudioInput = this._activeCall is not null && !this._activeCall.IsUserAudioInputPaused;
            if (resumeUserAudioInput)
            {
                this._activeCall!.PauseUserAudioInput();
            }

            try
            {
                return await this.InnerFunction.InvokeAsync(arguments, cancellationToken);
            }
            finally
            {
                if (resumeUserAudioInput &&
                    !this._activeCall!.CallToken.IsCancellationRequested &&
                    !this._activeCall.IsHangupAfterReplyPending(this._activeCall.TurnId))
                {
                    this._activeCall.ResumeUserAudioInput();
                }
            }
        }
    }
}
