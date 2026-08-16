using Agent.Telephone.Providers.LLM.Agents.Intent;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class IntentDetectionAgentTests
{
    [Fact]
    public async Task TryGetIntentDetectionResult_ReturnsFalseWhenTheResponseHasNoJsonAsync()
    {
        var agent = new ChatClientAgent(new EmptyChatClient(), new ChatClientAgentOptions());
        AgentResponse<IntentDetectionResult> response = await agent.RunAsync<IntentDetectionResult>("测试");

        Assert.False(IntentDetectionAgent.TryGetIntentDetectionResult(response, out IntentDetectionResult? result));
        Assert.Null(result);
    }

    private sealed class EmptyChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }
}
