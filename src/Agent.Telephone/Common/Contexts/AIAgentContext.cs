using Microsoft.Extensions.AI;

namespace Agent.Telephone.Common.Contexts
{
    internal class AIAgentContext : IDisposable
    {
        private readonly ActiveCallContext _activeCallContext;
        private readonly object _lifetimeLock = new();
        private readonly List<IDisposable> _ownedResources = [];
        private bool _disposed;

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

        /// <summary>
        /// Registers a resource whose lifetime is limited to this Agent session.
        /// </summary>
        public void RegisterOwnedResource(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);

            lock (this._lifetimeLock)
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                this._ownedResources.Add(resource);
            }
        }

        public void Dispose()
        {
            List<IDisposable> ownedResources;
            lock (this._lifetimeLock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                ownedResources = [.. this._ownedResources];
                this._ownedResources.Clear();
            }

            for (int index = ownedResources.Count - 1; index >= 0; index--)
            {
                ownedResources[index].Dispose();
            }

            this.PrivateProvider.Dispose();
            this.ChatHistory.Clear();
        }
    }
}
