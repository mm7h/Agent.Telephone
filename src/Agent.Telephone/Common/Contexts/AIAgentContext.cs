using Microsoft.Extensions.AI;

namespace Agent.Telephone.Common.Contexts
{
    internal class AIAgentContext : IDisposable
    {
        private readonly ActiveCallContext _activeCallContext;

        public AIAgentContext(ActiveCallContext activeCallContext)
        {
            this._activeCallContext = activeCallContext;
            this.PrivateProvider = new PrivateProvider(this._activeCallContext.DeviceId);
            this.AssistantPrompt = activeCallContext.AssistantConfig.Prompt;
            this.CurrentDialingNumber = activeCallContext.DialedNumber;
            this.ChatHistory = [];
        }
        public PrivateProvider PrivateProvider { get; }
        public string AssistantPrompt { get; set; }
        public List<ChatMessage> ChatHistory { get; }
        public string? CurrentDialingNumber { get; }

        public void Dispose()
        {
            this.PrivateProvider.Dispose();
            this.ChatHistory.Clear();
        }
    }
}
