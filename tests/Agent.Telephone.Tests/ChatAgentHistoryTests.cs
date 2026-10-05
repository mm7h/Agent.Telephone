using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Common.Contexts;
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

    [Theory]
    [InlineData("None")]
    [InlineData("FunctionCall")]
    public async Task StoresNormalTurnsInTheBoundSessionHistoryAsync(string intentType)
    {
        var chatClient = new ScriptedChatClient(["第一答。", "第二答。"]);
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_history", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var agent = new ChatAgent(serviceProvider, NullLogger<ChatAgent>.Instance);
        var historyProvider = new SessionChatHistoryProvider();
        List<ChatMessage> history = [new(ChatRole.System, "此前的对话")];
        historyProvider.Bind(history);
        PrivateProvider privateProvider = new("device");
        privateProvider.FunctionTools.Add(AIFunctionFactory.Create((Func<string>)(static () => "ok"), "UnusedTool"));
        var config = new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "history",
                Config = new Dictionary<string, string> { ["Prompt"] = "你是助手。", ["IntentType"] = intentType },
            },
            privateProvider,
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

    [Theory]
    [InlineData("", "任务已完成。", true, "RunTask", "任务已完成。")]
    [InlineData("这个任务会花一点时间。你可以先挂断电话。", "任务已完成。查询结果如下。", true, "RunTask", "任务已完成。查询结果如下。")]
    [InlineData("好的。再见。有需要随时找我。", "好的。再见。有需要随时找我。", false, "RunTask", "好的。再见。有需要随时找我。")]
    [InlineData("正在处理。", "请说再见。请说再见。", false, "RunTask", "请说再见。请说再见。")]
    [InlineData("", "好的。再见。好的。再见。", false, "HangupCurrentCall", "好的。再见。")]
    public async Task SpeaksOnlyFinalReplyAndPreservesToolHistoryAsync(
        string preToolText,
        string finalText,
        bool includeMessageIds,
        string functionName,
        string expectedText)
    {
        var chatClient = new FunctionCallingChatClient(preToolText, finalText, includeMessageIds, functionName);
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_tool-history", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var agent = new ChatAgent(serviceProvider, NullLogger<ChatAgent>.Instance);
        var privateProvider = new PrivateProvider("device");
        privateProvider.FunctionTools.Add(AIFunctionFactory.Create((Func<string>)(static () => "任务结果"), functionName));
        var historyProvider = new SessionChatHistoryProvider();
        List<ChatMessage> history = [];
        historyProvider.Bind(history);
        var config = new LLMAgentBuildConfig(
            new ModelSetting
            {
                ModelName = "tool-history",
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

        string spoken = await this.DrainResponseAsync(agent, "执行任务");

        Assert.Equal(expectedText, spoken);
        Assert.True(chatClient.ReceivedPairedToolMessages);
        Assert.Contains(history, message => message.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(history, message => message.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public void RemoveIncompleteFunctionCalls_DropsIncompleteFunctionCalls()
    {
        var historyProvider = new SessionChatHistoryProvider();
        List<ChatMessage> history =
        [
            new(ChatRole.User, "执行任务"),
            new(ChatRole.Assistant, [new FunctionCallContent("incomplete", "RunTask", new Dictionary<string, object?>())]),
        ];
        historyProvider.Bind(history);

        historyProvider.RemoveIncompleteFunctionCalls();
        IReadOnlyList<ChatMessage> messages = historyProvider.GetMessages();

        Assert.DoesNotContain(messages, message => message.Contents.OfType<FunctionCallContent>().Any());

        history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("incomplete", "RunTask", new Dictionary<string, object?>())]));
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("incomplete", "任务完成")]));
        historyProvider.RemoveIncompleteFunctionCalls();
        messages = historyProvider.GetMessages();

        Assert.Contains(messages, message => message.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(messages, message => message.Contents.OfType<FunctionResultContent>().Any());
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
    public async Task DirectIntentToolResponseIsEmittedOnceWithoutCallingTheLlmAsync()
    {
        var chatClient = new ScriptedChatClient([]);
        ServiceCollection services = new();
        services.AddKeyedSingleton<IChatClient>("LLM_intent", chatClient);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var functionCallAgent = new FunctionCallAgent(serviceProvider, NullLogger<FunctionCallAgent>.Instance);
        using var intentResponseAgent = new IntentResponseAgent(serviceProvider, NullLogger<IntentResponseAgent>.Instance);
        var privateProvider = new PrivateProvider("device");
        AIFunction function = AIFunctionFactory.Create((Func<FunctionReturn<string>>)(static () => new FunctionReturn<string>
        {
            Result = "感谢您的来电，再见。",
            Response = "感谢您的来电，再见。",
            Next = ToolAction.DirectResponse,
        }));
        privateProvider.AddFunctionToolRegistration(new FunctionToolRegistration(function, ToolAction.DirectResponse));
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
            "再见");

        await using StreamingRun run = await InProcessExecution.Concurrent.RunStreamingAsync(
            workflow,
            detection,
            "device",
            CancellationToken.None);
        await foreach (WorkflowEvent _ in run.WatchStreamAsync())
        {
        }

        Assert.Empty(chatClient.Requests);
        Assert.Collection(history,
            message => Assert.Equal((ChatRole.User, "再见"), (message.Role, message.Text)),
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Contains($"{function.Name} 执行结果：", message.Text, StringComparison.Ordinal);
                Assert.Contains("感谢您的来电，再见。", message.Text, StringComparison.Ordinal);
            },
            message => Assert.Equal((ChatRole.Assistant, "感谢您的来电，再见。"), (message.Role, message.Text)));
    }

    private async Task<string> DrainResponseAsync(ChatAgent agent, string userMessage)
    {
        var stream = (IAsyncEnumerable<string>)typeof(ChatAgent)
            .GetMethod("StreamLLMResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [userMessage, CancellationToken.None])!;
        System.Text.StringBuilder spoken = new();
        await foreach (string sentence in stream)
        {
            spoken.Append(sentence);
        }
        return spoken.ToString();
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

    private sealed class FunctionCallingChatClient(string preToolText, string finalText, bool includeMessageIds, string functionName) : IChatClient
    {
        public bool ReceivedPairedToolMessages { get; private set; }

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
            ChatMessage[] request = messages.ToArray();
            int functionResultIndex = Array.FindIndex(request, static message => message.Contents.OfType<FunctionResultContent>().Any());
            if (functionResultIndex >= 0)
            {
                this.ReceivedPairedToolMessages = request.Take(functionResultIndex)
                    .Any(static message => message.Contents.OfType<FunctionCallContent>().Any());
                if (!this.ReceivedPairedToolMessages)
                {
                    throw new InvalidOperationException("Function result was sent without its preceding function call.");
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, finalText) { MessageId = includeMessageIds ? "answer" : null };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, preToolText) { MessageId = includeMessageIds ? "call" : null };
            yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = includeMessageIds ? "call" : null,
                Contents = [new FunctionCallContent("call", functionName, new Dictionary<string, object?>())],
            };
        }

        public void Dispose()
        {
        }
    }
}
