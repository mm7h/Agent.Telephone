namespace Agent.Telephone.Abstractions.Configs
{
    public class PromptConfig
    {
        public PromptConfig()
        {
            this.HelloMessageTempletes = new List<string>();
        }
        public IList<string> HelloMessageTempletes { get; set; }
    }
}
