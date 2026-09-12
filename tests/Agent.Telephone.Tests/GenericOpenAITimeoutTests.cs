using System.Runtime.CompilerServices;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.LLM;
using Agent.Telephone.Providers.LLM.Agents;
using Agent.Telephone.Providers.LLM.Agents.Intent;
using Agent.Telephone.Providers.LLM.AIContextProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class GenericOpenAITimeoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task AssistantOverride_AllowsToolToOutlastModelTimeoutAsync(int timeoutSeconds)
    {
        using Fixture fixture = new();
        Assert.True(fixture.Build(timeoutSeconds));

        await fixture.Llm.StartDialogueAsync(1, "执行任务", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(fixture.ToolCompleted);
        Assert.Equal("completed", fixture.Callback.Outcome);
        Assert.Contains("完成", fixture.Callback.Text);
    }

    [Fact]
    public async Task OmittedOverride_UsesModelTimeoutAndCancelsToolAsync()
    {
        using Fixture fixture = new();
        Assert.True(fixture.Build(null));

        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Llm.StartDialogueAsync(1, "执行任务", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(fixture.ToolCompleted);
        Assert.Equal("failed", fixture.Callback.Outcome);
        Assert.IsType<TimeoutException>(fixture.Callback.Error);
    }

    [Fact]
    public async Task UnlimitedTimeout_StillHonorsCallerCancellationAsync()
    {
        using Fixture fixture = new();
        Assert.True(fixture.Build(0));
        using CancellationTokenSource cancellation = new();
        Task dialogue = fixture.Llm.StartDialogueAsync(1, "执行任务", cancellation.Token);
        await fixture.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialogue.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(fixture.ToolCompleted);
        Assert.Equal("cancelled", fixture.Callback.Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4294968)]
    public void Build_RejectsInvalidTimeout(int timeoutSeconds)
    {
        using Fixture fixture = new();
        Assert.False(fixture.Build(timeoutSeconds));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SIPTransport _transport = new();
        private readonly ServiceProvider _services;
        private readonly DeviceContext _device;
        private readonly ActiveCallContext _call;
        public GenericOpenAI Llm { get; }
        public RecordingCallback Callback { get; } = new();
        public TaskCompletionSource<bool> ToolStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ToolCompleted { get; private set; }

        public Fixture()
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddKeyedSingleton<IChatClient>("LLM_test", new ToolChatClient());
            services.AddKeyedTransient<IAgent, InputAgent>(SubAgentNames.InputAgent);
            services.AddKeyedTransient<IAgent, ChatAgent>(SubAgentNames.ChatAgent);
            services.AddKeyedTransient<IAgent, OutputAgent>(SubAgentNames.OutputAgent);
            services.AddKeyedTransient<IAgent, IntentDetectionAgent>(SubAgentNames.IntentDetectionAgent);
            services.AddKeyedTransient<IAgent, FunctionCallAgent>(SubAgentNames.FunctionCallAgent);
            services.AddKeyedTransient<IAgent, IntentResponseAgent>(SubAgentNames.IntentResponseAgent);
            this._services = services.BuildServiceProvider();
            this.Llm = new GenericOpenAI(
                this._services,
                new SessionChatHistoryProvider(),
                new DefaultObjectPool<OutSegment>(new DefaultPooledObjectPolicy<OutSegment>()),
                NullLogger<GenericOpenAI>.Instance);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            this._device = new DeviceContext(
                this._transport,
                new DeviceRegistrationRecord("test", "sip:user@test", "sip:user@192.0.2.1", now, now, now.AddMinutes(5)),
                [new AssistantConfig { DialingNumber = "10088" }]);
            AudioExtrasSource source = new(
                new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
            this._call = new ActiveCallContext(
                this._device,
                "sip:user@test",
                "10088",
                new SIPUserAgent(this._transport, SIPEndPoint.Empty, false),
                new VoIPMediaSession(new MediaEndPoints { AudioSource = source }));
            this._call.AIAgentContext.PrivateProvider.FunctionTools.Add(AIFunctionFactory.Create(this.RunToolAsync, "LongTask"));
        }

        public bool Build(int? timeoutSeconds)
        {
            Dictionary<string, ModelSetting> settings = new()
            {
                [SubAgentNames.InputAgent] = ModelSetting.Empty,
                [SubAgentNames.IntentDetectionAgent] = ModelSetting.Empty,
                [SubAgentNames.FunctionCallAgent] = ModelSetting.Empty,
                [SubAgentNames.IntentResponseAgent] = ModelSetting.Empty,
                [SubAgentNames.OutputAgent] = ModelSetting.Empty,
                [SubAgentNames.ChatAgent] = new ModelSetting
                {
                    ModelName = "test",
                    Config = new Dictionary<string, string>
                    {
                        ["Prompt"] = "你是助手。",
                        ["IntentType"] = "FunctionCall",
                        ["ResponseTimeoutSeconds"] = "1"
                    }
                }
            };
            bool built = this.Llm.Build(new LLMBuildConfig(settings, this._call.AIAgentContext.PrivateProvider, timeoutSeconds));
            if (built)
            {
                this.Llm.RegisterDevice(this._call, this.Callback);
            }
            return built;
        }

        private async Task<string> RunToolAsync(CancellationToken cancellationToken)
        {
            this.ToolStarted.TrySetResult(true);
            await Task.Delay(1500, cancellationToken);
            this.ToolCompleted = true;
            return "任务完成";
        }

        public void Dispose()
        {
            this.Llm.Dispose();
            this._call.Dispose();
            this._device.Dispose();
            this._transport.Dispose();
            this._services.Dispose();
        }
    }

    private sealed class ToolChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any()))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "任务完成。") { MessageId = "answer" };
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                {
                    MessageId = "tool",
                    Contents = [new FunctionCallContent("call", "LongTask", new Dictionary<string, object?>())]
                };
            }
        }

        public void Dispose() { }
    }

    private sealed class RecordingCallback : ILlmEventCallback
    {
        public string? Outcome { get; private set; }
        public Exception? Error { get; private set; }
        public string Text { get; private set; } = string.Empty;
        public Task OnBeforeFirstSegmentAsync(long turnId, OutSegment firstSegment, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnSegmentAsync(long turnId, OutSegment segment, CancellationToken cancellationToken)
        {
            this.Text += segment.Content;
            return Task.CompletedTask;
        }
        public Task OnCompletedAsync(long turnId, CancellationToken cancellationToken)
        {
            this.Outcome = "completed";
            return Task.CompletedTask;
        }
        public Task OnCancelledAsync(long turnId, CancellationToken cancellationToken)
        {
            this.Outcome = "cancelled";
            return Task.CompletedTask;
        }
        public Task OnFailedAsync(long turnId, Exception exception, CancellationToken cancellationToken)
        {
            this.Outcome = "failed";
            this.Error = exception;
            return Task.CompletedTask;
        }
    }
}
