namespace Agent.Telephone.Abstractions.Configs
{
    public class AssistantConfig
    {
        public AssistantConfig()
        {
            this.DialingNumber = string.Empty;
            this.Name = string.Empty;
            this.Prompt = string.Empty;
            this.VAD = string.Empty;
            this.ASR = string.Empty;
            this.Intent = string.Empty;
            this.LLM = string.Empty;
            this.TTS = string.Empty;
            this.TTSSettings = [];
            this.AllowedTools = [];
            this.HelloMessageTempletes = [];
        }
        public string DialingNumber { get; set; }
        public string Name { get; set; }
        public string Prompt { get; set; }
        public string VAD { get; set; }
        public string ASR { get; set; }
        public string Intent { get; set; }
        public string LLM { get; set; }
        /// <summary>每轮对话（含工具执行）的最长秒数；null 沿用 LLM 模型配置，0 不限制时长。</summary>
        public int? LLMResponseTimeoutSeconds { get; set; }
        public string TTS { get; set; }
        public List<Dictionary<string, string>> TTSSettings { get; set; }
        public List<string> AllowedTools { get; set; }
        public List<string> HelloMessageTempletes { get; set; }
    }
}
