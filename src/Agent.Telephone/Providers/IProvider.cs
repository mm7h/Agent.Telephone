namespace Agent.Telephone.Providers
{
    using Agent.Telephone.Common.Contexts;

    internal interface IProvider<TSettings> : IDisposable where TSettings : class
    {
        string ProviderType { get; }
        string ModelName { get; }
        public bool IsSherpaModel { get; }
        bool Build(TSettings settings);
        void RegisterDevice(ActiveCallContext activeCall);
        void UnregisterDevice(ActiveCallContext activeCall);
    }
}
