using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using System;
using System.Text.RegularExpressions;
using Agent.Telephone.Common.Configs;

namespace Agent.Telephone.Providers.LLM.Agents
{
    internal abstract class BaseAgent<TLogger> : Executor, IAgent
    {
        protected BaseAgent(string agentName, IServiceProvider serviceProvider, ILogger<TLogger> logger) : base(agentName, declareCrossRunShareable: true)
        {
            this.ServiceProvider = serviceProvider;
            this.Logger = logger;
            this.AgentName = agentName;
        }
        public IServiceProvider ServiceProvider { get; set; }
        public string AgentName { get; }
        public string Prompt { get; protected set; } = "You are a helpful assistant.";
        public abstract int Order { get; }
        public virtual bool IsEnabled { get; protected set; } = true;
        public virtual bool SupportsStreaming { get; protected set; } = true;
        protected ILogger<TLogger> Logger { get; }
        protected string DeviceId { get; set; } = string.Empty;
        public abstract bool Build(LLMAgentBuildConfig buildConfig);
        public abstract void Dispose();

        public Executor AsExecutor() => this;

        public virtual void RegisterDevice(string deviceId)
        {
            this.DeviceId = deviceId;
            this.Logger.LogInformation("设备 {deviceId} 已经在 {agentName} 注册", this.DeviceId, this.AgentName);
        }

        public virtual void UnregisterDevice(string deviceId)
        {
            this.Logger.LogInformation("设备 {deviceId} 已经在 {agentName} 注销", deviceId, this.AgentName);
            this.DeviceId = string.Empty;
        }

        public virtual bool CheckDeviceRegistered(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(this.DeviceId))
            {
                this.Logger.LogWarning("设备 {deviceId} 在 {agentName} 未注册", string.IsNullOrWhiteSpace(deviceId) ? "unkonwn" : deviceId, this.AgentName);
                return false;
            }
            return true;
        }

        protected virtual string GenerateId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
