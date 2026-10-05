using Agent.Telephone.Abstractions.FunctionTools;

namespace Agent.Telephone.FunctionTools
{
    public abstract class PrivateFunctionTool : FunctionTool, IPrivateFunctionTool
    {
        protected PrivateFunctionTool()
        {
        }

        public IDeviceContext DeviceContext { get; internal set; } = null!;

        public IAssistantControl CallControl { get; internal set; } = null!;

        public virtual ValueTask OnDeviceConnectedAsync()
        {
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask OnDeviceClosedAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
