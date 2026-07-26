namespace Agent.Telephone.Abstractions.FunctionTools
{
    public interface IPrivateFunctionTool : IFunctionTool
    {
        /// <summary>
        /// 当前设备 calling 上下文
        /// </summary>
        IDeviceContext DeviceContext { get; }
        /// <summary>
        /// 当前通话的控制接口。
        /// </summary>
        ICallControl CallControl { get; }
        /// <summary>
        /// 当设备连接时触发
        /// </summary>
        /// <returns></returns>
        ValueTask OnDeviceConnectedAsync();
        /// <summary>
        /// 当设备关闭时触发
        /// </summary>
        /// <returns></returns>
        ValueTask OnDeviceClosedAsync();
    }
}
