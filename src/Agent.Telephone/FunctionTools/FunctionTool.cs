using Agent.Telephone.Abstractions.FunctionTools;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.FunctionTools
{
    public abstract class FunctionTool : IFunctionTool
    {
        protected FunctionTool()
        {
        }

        public ILogger Logger { get; internal set; } = null!;

        public IServerInfo ServerInfo { get; internal set; } = null!;

        public virtual ValueTask OnFunctionToolInitializedAsync()
        {
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask OnFunctionToolReleasedAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
