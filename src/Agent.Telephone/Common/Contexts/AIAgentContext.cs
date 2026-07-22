using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Telephone.Common.Contexts
{
    internal class AIAgentContext : IDisposable
    {
        private readonly DeviceContext _deviceContext;

        public AIAgentContext(DeviceContext deviceContext)
        {
            this._deviceContext = deviceContext;
            this.PrivateProvider = new PrivateProvider(deviceContext.DeviceId);
            this.ChatHistory = [];
        }
        public PrivateProvider PrivateProvider { get; }
        public List<ChatMessage> ChatHistory { get; }
        public string? CurrentDialingNumber { get; set; }
        public string? LastDialingNumber { get; set; }

        public void Dispose()
        {
            this.CurrentDialingNumber = null;
            this.LastDialingNumber = null;
            this.PrivateProvider.Dispose();
            this.ChatHistory.Clear();
        }
    }
}
