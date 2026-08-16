using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.FunctionTools
{
    internal sealed class DtmfGatedAIFunction : DelegatingAIFunction
    {
        private readonly IAssistantControl _assistantControl;
        private readonly DtmfKey _acceptedKeys;
        private readonly string _resultParameterName;

        public DtmfGatedAIFunction(AIFunction innerFunction, IAssistantControl assistantControl, DtmfKey acceptedKeys, string resultParameterName) : base(innerFunction)
        {
            this._assistantControl = assistantControl;
            this._acceptedKeys = acceptedKeys;
            this._resultParameterName = resultParameterName;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            DtmfInputResult input = await this._assistantControl.RequestDtmfInputAsync(
                this._acceptedKeys,
                cancellationToken);
            arguments[this._resultParameterName] = input;
            return await this.InnerFunction.InvokeAsync(arguments, cancellationToken);
        }
    }
}
