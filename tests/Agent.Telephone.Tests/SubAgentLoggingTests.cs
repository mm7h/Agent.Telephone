using Agent.Telephone.Providers.LLM.Agents;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class SubAgentLoggingTests
{
    [Fact]
    public void UnregisterDeviceLogsTheProvidedDeviceId()
    {
        var logger = new CapturingLogger<InputAgent>();
        using var agent = new InputAgent(null!, logger);

        agent.UnregisterDevice("device-123");

        Assert.Equal("设备 device-123 已经在 InputAgent 注销", logger.Message);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public string? Message { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Message = formatter(state, exception);
        }
    }
}
