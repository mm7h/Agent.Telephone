namespace Agent.Telephone.Abstractions.Configs
{
    public sealed class PromptMediaConfig
    {
        public PromptMediaConfig()
        {
            this.Code = 0;
            this.FilePath = string.Empty;
        }

        public int Code { get; set; }
        public string FilePath { get; set; }
        public string? Description { get; set; }
    }
}
