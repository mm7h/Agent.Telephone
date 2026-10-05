namespace Agent.Telephone.Abstractions.Configs
{
    public sealed class ModelConfig
    {
        public Dictionary<string, string> SelectedDefaultSettings { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ConfiguredSettings { get; set; } = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();

    }

    public sealed class ModelSetting
    {
        public static readonly ModelSetting Empty = new ModelSetting();
        public string ModelName { get; set; } = null!;
        public Dictionary<string, string> Config { get; set; } = new Dictionary<string, string>();
    }
}
