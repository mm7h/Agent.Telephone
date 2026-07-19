using Agent.Telephone.Abstractions.Configs;

namespace Agent.Telephone.Resources
{
    internal interface IOnnxModel : IResource<ModelSetting>
    {
        public string ModelType { get; }
        public string ModelName { get; }
    }
}
