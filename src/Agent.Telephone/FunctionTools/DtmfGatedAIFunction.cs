using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.FunctionTools
{
    internal sealed class DtmfGatedAIFunction : DelegatingAIFunction
    {
        private readonly AIAgentContext _agentContext;
        private readonly DtmfKey _acceptedKeys;
        private readonly string _prompt;
        private readonly string _resultParameterName;

        public DtmfGatedAIFunction(
            AIFunction innerFunction,
            AIAgentContext agentContext,
            DtmfKey acceptedKeys,
            string prompt,
            string resultParameterName) : base(innerFunction)
        {
            this._agentContext = agentContext;
            this._acceptedKeys = acceptedKeys;
            this._prompt = prompt;
            this._resultParameterName = resultParameterName;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            DtmfInputResult input = await this._agentContext.RequestDtmfInteractionAsync(
                this._prompt,
                this._acceptedKeys,
                cancellationToken);
            arguments[this._resultParameterName] = input;
            return await this.InnerFunction.InvokeAsync(arguments, cancellationToken);
        }
    }
}
