namespace Agent.Telephone.Abstractions.Configs
{
    public class TelephoneConfig
    {
        public TelephoneConfig() 
        {
            this.SIPConfig = default!;
            this.ModelConfig = default!;
        }

        public bool AuthEnabled { get; set; }
        public LogSetting LogSetting { get; set; } = new LogSetting();
        public SIPConfig SIPConfig { get; init; }
        public ModelConfig ModelConfig { get; init; }
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
