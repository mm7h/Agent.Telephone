using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Abstractions.Configs
{
    public sealed class ModelConfig
    {
        public ServerProtocol ServerProtocol { get; set; }
        public string Prompt { get; set; } = string.Empty;
        public Dictionary<string, string> SelectedSettings { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ConfiguredSettings { get; set; } = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
        public AudioSetting AudioSetting { get; set; } = null!;
        public Dictionary<string, ModelSetting>? McpSettings { get; set; }

    }

    #region WebSocketSetting
    public sealed class WebSocketServerOption
    {
        public string IP { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 4530;
        public string Path { get; set; } = "/xiaozhi/v1/";
        public WssOption? WssOption { get; set; }
    }
    public sealed class WssOption
    {
        public string CertFilePath { get; set; } = "";
        public string? CertPassword { get; set; }
    }
    #endregion

    public sealed class ModelSetting
    {
        public string ModelName { get; set; } = null!;
        public Dictionary<string, string> Config { get; set; } = new Dictionary<string, string>();
    }
    public sealed class AudioSetting
    {
        public AudioSetting()
        {

        }

        public AudioSetting(string format, int sampleRate, int channels, int frameDuration)
        {
            Format = format;
            SampleRate = sampleRate;
            Channels = channels;
            FrameDuration = frameDuration;
        }

        public string Format { get; set; } = "opus";
        public int SampleRate { get; set; } = 24000;
        public int Channels { get; set; } = 1;
        public int FrameDuration { get; set; } = 60;
        public int FrameSize => SampleRate * FrameDuration * Channels / 1000;
    }

}
