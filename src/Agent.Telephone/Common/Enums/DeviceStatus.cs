namespace Agent.Telephone.Common.Enums
{
    internal enum DeviceStatus
    {
        Unknown = 0,
        Offline,
        Registering,
        Registered,
        Idle,
        Calling,
        Busy,
        TransferringCall,
        TransferredCall
    }
}
