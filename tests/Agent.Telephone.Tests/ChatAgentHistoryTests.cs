using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.LLM.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class ChatAgentHistoryTests
{
    [Fact]
    public async Task OmitsTextResponseFormatWhenFunctionCallingAsync()
    {
        var chatClient = new CapturingChatClient();
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_tool", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var agent = new ChatAgent(serviceProvider, NullLogger<ChatAgent>.Instance);
        var privateProvider = new PrivateProvider("device");
        privateProvider.FunctionTools.Add(AIFunctionFactory.Create((Func<string>)(static () => "ok")));
        var config = new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "tool",
                Config = new Dictionary<string, string>
                {
                    ["Prompt"] = "你是助手。",
                    ["IntentType"] = "FunctionCall",
                },
            },
            privateProvider);

        Assert.True(agent.Build(config));
        agent.RegisterDevice("device");

        var stream = (IAsyncEnumerable<string>)typeof(ChatAgent)
            .GetMethod("StreamLLMResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, ["测试", CancellationToken.None])!;
        await foreach (string _ in stream)
        {
        }

        Assert.NotNull(chatClient.LastOptions);
        Assert.Equal(ChatToolMode.Auto, chatClient.LastOptions!.ToolMode);
        Assert.NotEmpty(chatClient.LastOptions.Tools!);
        Assert.Null(chatClient.LastOptions.ResponseFormat);
    }

    [Fact]
    public void UsesTheAssignedCallChatHistory()
    {
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_history", new StubChatClient());
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var agent = new ChatAgent(serviceProvider, NullLogger<ChatAgent>.Instance);
        var config = new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "history",
                Config = new Dictionary<string, string> { ["Prompt"] = "你是助手。" },
            },
            new PrivateProvider("device"));

        Assert.True(agent.Build(config));
        List<ChatMessage> history = [new(ChatRole.System, "此前的对话")];

        agent.SetChatHistory(history);

        Assert.Single(history);
        Assert.Equal("此前的对话", history[0].Text);
        var session = Assert.IsAssignableFrom<AgentSession>(typeof(ChatAgent)
            .GetField("_agentSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(agent));
        Assert.True(session.TryGetInMemoryChatHistory(
            out List<ChatMessage>? attachedHistory,
            jsonSerializerOptions: JsonHelper.OPTIONS));
        Assert.Same(history, attachedHistory);
    }

    private sealed class StubChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

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

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastOptions = options;
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }
}
