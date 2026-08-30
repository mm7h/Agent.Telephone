using Agent.Telephone.Sample.Server.FunctionTools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class CodexAssistantTests
{
    private static readonly SemaphoreSlim s_environmentLock = new(1, 1);

    [Fact]
    public async Task RunCodexTaskAsync_ReturnsTheCliFinalOutputAsync()
    {
        await s_environmentLock.WaitAsync();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"codex-test-{Guid.NewGuid():N}.cmd");
        string? previousExecutablePath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        try
        {
            await File.WriteAllTextAsync(scriptPath, "@echo off\r\necho Codex completed the requested task.\r\nexit /b 0\r\n");
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", scriptPath);

            var tool = new CodexAssistant
            {
                Logger = NullLogger.Instance,
            };

            var result = await tool.RunCodexTaskAsync("summarize the task");

            Assert.Equal("Codex completed the requested task.", result.Result);
            Assert.Equal(result.Result, result.Response);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", previousExecutablePath);
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
            s_environmentLock.Release();
        }
    }

}
