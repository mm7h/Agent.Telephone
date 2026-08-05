namespace Agent.Telephone.Sample.Server.MessageStore
{
    public sealed class SqliteMessageStoreOptions
    {
        public string DatabasePath { get; set; } = "./data/messages-db/messages.db";
        public int RetentionDays { get; set; } = 30;
        public int MaxMessagesPerConversation { get; set; } = 100;
        public int DefaultTimeoutSeconds { get; set; } = 5;
        public bool UseLogicalDelete { get; set; } = true;
    }
}
