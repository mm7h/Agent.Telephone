using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers.LLM.AIContextProviders;
using Agent.Telephone.Providers.LLM.Agents;
using Agent.Telephone.Providers.LLM.Agents.Intent;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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
        var historyProvider = new SessionChatHistoryProvider();
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
            privateProvider,
            historyProvider);

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
        Assert.Equal(ReasoningEffort.None, chatClient.LastOptions.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.None, chatClient.LastOptions.Reasoning.Output);
    }

    [Fact]
    public async Task StoresNormalTurnsInTheBoundSessionHistoryAsync()
    {
        var chatClient = new ScriptedChatClient(["第一答。", "第二答。"]);
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_history", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var agent = new ChatAgent(serviceProvider, NullLogger<ChatAgent>.Instance);
        var historyProvider = new SessionChatHistoryProvider();
        List<ChatMessage> history = [new(ChatRole.System, "此前的对话")];
        historyProvider.Bind(history);
        var config = new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "history",
                Config = new Dictionary<string, string> { ["Prompt"] = "你是助手。" },
            },
            new PrivateProvider("device"),
            historyProvider);

        Assert.True(agent.Build(config));
        agent.RegisterDevice("device");

        await this.DrainResponseAsync(agent, "第一问");
        await this.DrainResponseAsync(agent, "第二问");

        Assert.Collection(history,
            message => Assert.Equal((ChatRole.System, "此前的对话"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.User, "第一问"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.Assistant, "第一答。"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.User, "第二问"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.Assistant, "第二答。"), (message.Role, message.Text)));

        IReadOnlyList<ChatMessage> secondRequest = Assert.Single(chatClient.Requests.Skip(1));
        Assert.Collection(secondRequest,
            message => Assert.Equal((ChatRole.System, "此前的对话"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.User, "第一问"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.Assistant, "第一答。"), (message.Role, message.Text)),
            message => Assert.Equal((ChatRole.User, "第二问"), (message.Role, message.Text)));
    }

    [Fact]
    public async Task IntentLlmAddsToolCapabilitiesWithoutExecutableToolsAsync()
    {
        var chatClient = new CapturingChatClient();
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_intent", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var agent = new ChatAgent(serviceProvider, NullLogger<ChatAgent>.Instance);
        var privateProvider = new PrivateProvider("device");
        using JsonDocument schemaDocument = JsonDocument.Parse("{}");
        privateProvider.FunctionTools.Add(AIFunctionFactory.CreateDeclaration(
            "GetWeather",
            "查询指定城市的天气。",
            schemaDocument.RootElement.Clone()));
        var config = new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "intent",
                Config = new Dictionary<string, string>
                {
                    ["Prompt"] = "你是助手。",
                    ["IntentType"] = "IntentLlm",
                },
            },
            privateProvider,
            new SessionChatHistoryProvider());

        Assert.True(agent.Build(config));
        agent.RegisterDevice("device");

        await this.DrainResponseAsync(agent, "你有什么能力？");

        Assert.NotNull(chatClient.LastOptions);
        Assert.Null(chatClient.LastOptions!.Tools);
        Assert.NotEqual(ChatToolMode.Auto, chatClient.LastOptions.ToolMode);
        Assert.Contains("查询指定城市的天气。", chatClient.LastOptions.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoresIntentToolResultAndBroadcastResponseInSessionHistoryAsync()
    {
        var chatClient = new ScriptedChatClient(["北京今天晴天。"]);
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_intent", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var functionCallAgent = new FunctionCallAgent(serviceProvider, NullLogger<FunctionCallAgent>.Instance);
        using var intentResponseAgent = new IntentResponseAgent(serviceProvider, NullLogger<IntentResponseAgent>.Instance);
        var privateProvider = new PrivateProvider("device");
        AIFunction function = AIFunctionFactory.Create((Func<string>)(static () => "北京晴天"));
        privateProvider.FunctionTools.Add(function);
        var historyProvider = new SessionChatHistoryProvider();
        List<ChatMessage> history = [];
        historyProvider.Bind(history);

        Assert.True(functionCallAgent.Build(new LLMAgentBuildConfig(ModelSetting.Empty, privateProvider, historyProvider)));
        Assert.True(intentResponseAgent.Build(new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "intent",
                Config = new Dictionary<string, string>
                {
                    ["Type"] = "IntentLlm",
                    ["LLM"] = "intent",
                },
            },
            privateProvider,
            historyProvider)));
        functionCallAgent.RegisterDevice("device");
        intentResponseAgent.RegisterDevice("device");

        ExecutorBinding functionCallExecutor = functionCallAgent.AsExecutor();
        ExecutorBinding intentResponseExecutor = intentResponseAgent.AsExecutor();
        Workflow workflow = new WorkflowBuilder(functionCallExecutor)
            .AddEdge(functionCallExecutor, intentResponseExecutor)
            .WithOutputFrom(intentResponseExecutor)
            .Build();
        var detection = new IntentDetectionResult(
            true,
            new FunctionMetadata { Name = function.Name },
            "北京天气怎么样？");

        await using StreamingRun run = await InProcessExecution.Concurrent.RunStreamingAsync(
            workflow,
            detection,
            "device",
            CancellationToken.None);
        await foreach (WorkflowEvent _ in run.WatchStreamAsync())
        {
        }

        Assert.Collection(history,
            message => Assert.Equal((ChatRole.User, "北京天气怎么样？"), (message.Role, message.Text)),
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Contains($"{function.Name} 执行结果：", message.Text, StringComparison.Ordinal);
                Assert.Contains("北京晴天", message.Text, StringComparison.Ordinal);
            },
            message => Assert.Equal((ChatRole.Assistant, "北京今天晴天。"), (message.Role, message.Text)));
    }

    private async Task DrainResponseAsync(ChatAgent agent, string userMessage)
    {
        var stream = (IAsyncEnumerable<string>)typeof(ChatAgent)
            .GetMethod("StreamLLMResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [userMessage, CancellationToken.None])!;
        await foreach (string _ in stream)
        {
        }
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _responses;

        public ScriptedChatClient(IEnumerable<string> responses)
        {
            this._responses = new Queue<string>(responses);
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
            this.Requests.Add(messages.Select(static message => new ChatMessage(message.Role, message.Text)).ToArray());
            yield return new ChatResponseUpdate(ChatRole.Assistant, this._responses.Dequeue())
            {
                MessageId = Guid.NewGuid().ToString("N")
            };
            await Task.CompletedTask;
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
