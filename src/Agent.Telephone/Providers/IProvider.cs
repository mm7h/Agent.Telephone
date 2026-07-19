namespace Agent.Telephone.Providers
{
    internal interface IProvider<TSettings> : IDisposable where TSettings : class
    {
        string ProviderType { get; }
        string ModelName { get; }
        public bool IsSherpaModel { get; }
        bool Build(TSettings settings);
        void RegisterDevice(string deviceId);
        void UnregisterDevice(string deviceId);
    }
}
