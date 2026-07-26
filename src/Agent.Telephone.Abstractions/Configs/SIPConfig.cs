namespace Agent.Telephone.Abstractions.Configs
{
    public class SIPConfig
    {
        public string IP { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 5060;
        public string FFmpegPath { get; set; } = "./ffmpeg/";
        public int HangUpTimeoutSeconds { get; set; } = 30;
        public int AgentInitializationTimeoutSeconds { get; set; } = 30;
        public int TransferTimeoutSeconds { get; set; } = 30;
        public int CallbackTimeoutSeconds { get; set; } = 30;
    }
}
