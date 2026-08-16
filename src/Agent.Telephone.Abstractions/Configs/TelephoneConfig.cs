namespace Agent.Telephone.Abstractions.Configs
{
    public class TelephoneConfig
    {
        public TelephoneConfig() 
        {
            this.SIPConfig = default!;
            this.ModelConfig = default!;
            this.PromptConfig = default!;
            this.AssistantConfigs = [];
            this.LogSetting = new();
        }

        public bool AuthEnabled { get; set; }
        public LogSetting LogSetting { get; set; }
        public SIPConfig SIPConfig { get; set; }
        public List<AssistantConfig> AssistantConfigs { get; set; }
        public ModelConfig ModelConfig { get; set; }
        public PromptConfig PromptConfig { get; set; }
    }

    #region Log
    public sealed class LogSetting
    {
        public string LogLevel { get; set; } = "INFO";
        public string LogFilePath { get; set; } = "logs/server_log.log";
        public string OutputTemplate { get; set; } = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u4}] {Message:lj}{NewLine}{Exception}";
        public int RetainedFileCountLimit { get; set; } = 7;
    }
    #endregion
}
