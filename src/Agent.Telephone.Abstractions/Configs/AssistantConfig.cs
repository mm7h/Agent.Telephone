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
            this.Memory = string.Empty;
            this.AllowedTools = [];
            this.Capabilities = [];
        }
        public string DialingNumber { get; set; }
        public string Name { get; set; }
        public string Prompt { get; set; }
        public string VAD { get; set; }
        public string ASR { get; set; }
        public string Intent { get; set; }
        public string LLM { get; set; }
        public string TTS { get; set; }
        public string Memory { get; set; }
        public List<string> AllowedTools { get; set; }
        public List<string> Capabilities { get; set; }
    }
}
