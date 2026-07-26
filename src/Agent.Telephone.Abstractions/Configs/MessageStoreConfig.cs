namespace Agent.Telephone.Abstractions.Configs
{
    public sealed class MessageStoreConfig
    {
        public string RootPath { get; set; } = "./data/messages";
        public int RetentionDays { get; set; } = 30;
        public int MaxMessagesPerConversation { get; set; } = 100;
        public int RecentConversationTurns { get; set; } = 20;
    }
}
