using System.Runtime.CompilerServices;
using Agent.Telephone.Abstractions.Common.Enums;
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
using Agent.Telephone.Providers.LLM.Contexts;
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
    private const string PreExecutionPrompt = "好的任务正在处理请稍等";

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
    [InlineData("IntentLlm")]
    [InlineData("FunctionCall")]
    public async Task PreExecutionPrompt_IsDeliveredBeforeLongRunningToolCompletesAsync(string intentType)
    {
        using Fixture fixture = new();
        Assert.True(fixture.Build(5, intentType));

        Task dialogue = fixture.Llm.StartDialogueAsync(1, "执行任务", CancellationToken.None);
        await fixture.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Callback.PreExecutionPromptDelivered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(fixture.ToolCompleted);
        Assert.DoesNotContain("任务完成", fixture.Callback.Text);

        await dialogue.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Collection(
            fixture.Callback.Segments,
            segment =>
            {
                Assert.Equal(PreExecutionPrompt, segment.Content);
                Assert.True(segment.IsFirst);
                Assert.True(segment.IsLast);
            },
            segment =>
            {
                Assert.Equal("任务完成", segment.Content);
                Assert.True(segment.IsFirst);
                Assert.True(segment.IsLast);
            });
        Assert.Equal("completed", fixture.Callback.Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4294968)]
    public void Build_RejectsInvalidTimeout(int timeoutSeconds)
    {
        using Fixture fixture = new();
        Assert.False(fixture.Build(timeoutSeconds));
    }

    [Theory]
    [InlineData("IntentLlm")]
    [InlineData("FunctionCall")]
    public async Task ToolInvocation_WaitsForPromptPlaybackAsync(string intentType)
    {
        using Fixture fixture = new();
        fixture.Callback.HoldPromptPlayback = true;
        Assert.True(fixture.Build(0, intentType));
        Task dialogue = fixture.Llm.StartDialogueAsync(1, "执行任务", CancellationToken.None);
        await fixture.Callback.PreExecutionPromptDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.ToolStarted.Task.IsCompleted);

        fixture.Callback.PromptPlaybackCompleted.SetResult(true);
        await fixture.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Call.IsUserAudioInputPaused);
        await dialogue.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.ToolCompleted);
        Assert.False(fixture.Call.IsUserAudioInputPaused);
    }

    [Fact]
    public async Task CancelledPrompt_DoesNotStartToolAsync()
    {
        using Fixture fixture = new();
        fixture.Callback.HoldPromptPlayback = true;
        Assert.True(fixture.Build(0, "FunctionCall"));
        using CancellationTokenSource cancellation = new();
        Task dialogue = fixture.Llm.StartDialogueAsync(1, "执行任务", cancellation.Token);
        await fixture.Callback.PreExecutionPromptDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialogue.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(fixture.ToolStarted.Task.IsCompleted);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SIPTransport _transport = new();
        private readonly ServiceProvider _services;
        private readonly DeviceContext _device;
        private readonly ActiveCallContext _call;
        public GenericOpenAI Llm { get; }
        public ActiveCallContext Call => this._call;
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
                TestServices.ScopeFactory,
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
            AIFunction tool = AIFunctionFactory.Create(this.RunToolAsync, "LongTask");
            this._call.AIAgentContext.PrivateProvider.AddFunctionToolRegistration(
                new FunctionToolRegistration(tool, ToolAction.Continue, PreExecutionPrompt));
        }

        public bool Build(int? timeoutSeconds, string intentType = "IntentLlm")
        {
            Dictionary<string, ModelSetting> settings = new()
            {
                [SubAgentNames.InputAgent] = new ModelSetting
                {
                    Config = new Dictionary<string, string>
                    {
                        ["IntentType"] = intentType,
                    },
                },
                [SubAgentNames.IntentDetectionAgent] = new ModelSetting
                {
                    ModelName = "test",
                    Config = new Dictionary<string, string>
                    {
                        ["Type"] = "IntentLlm",
                        ["LLM"] = "test",
                    },
                },
                [SubAgentNames.FunctionCallAgent] = ModelSetting.Empty,
                [SubAgentNames.IntentResponseAgent] = new ModelSetting
                {
                    ModelName = "test",
                    Config = new Dictionary<string, string>
                    {
                        ["Type"] = "IntentLlm",
                        ["LLM"] = "test",
                    },
                },
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
            Assert.True(this.Callback.PreExecutionPromptDelivered.Task.IsCompletedSuccessfully);
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
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "{\"detected\":true,\"function\":{\"name\":\"LongTask\",\"parameters\":[]},\"userMessage\":\"执行任务\"}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (options?.Instructions?.Contains("意图识别助手", StringComparison.Ordinal) == true)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "{\"detected\":true,\"function\":{\"name\":\"LongTask\",\"parameters\":[]},\"userMessage\":\"执行任务\"}") { MessageId = "intent" };
            }
            else if (options?.Instructions?.Contains("意图识别结果播报助手", StringComparison.Ordinal) == true)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "任务完成。") { MessageId = "answer" };
            }
            else if (!messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any()))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "这个任务会花一点时间。你可以先挂断电话。") { MessageId = "tool-call" };
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call", "LongTask")])
                {
                    MessageId = "tool-call",
                    FinishReason = ChatFinishReason.ToolCalls,
                };
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "任务完成。") { MessageId = "answer" };
            }
        }

        public void Dispose() { }
    }

    private sealed class RecordingCallback : ILlmEventCallback
    {
        public string? Outcome { get; private set; }
        public Exception? Error { get; private set; }
        public string Text { get; private set; } = string.Empty;
        public List<RecordedSegment> Segments { get; } = [];
        public TaskCompletionSource<bool> PreExecutionPromptDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PromptPlaybackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldPromptPlayback { get; set; }
        public async Task OnToolExecutionPromptAsync(long turnId, OutSegment segment, CancellationToken cancellationToken)
        {
            await this.OnSegmentAsync(turnId, segment, cancellationToken);
            if (this.HoldPromptPlayback)
            {
                await this.PromptPlaybackCompleted.Task.WaitAsync(cancellationToken);
            }
        }
        public Task OnBeforeFirstSegmentAsync(long turnId, OutSegment firstSegment, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnSegmentAsync(long turnId, OutSegment segment, CancellationToken cancellationToken)
        {
            this.Text += segment.Content;
            this.Segments.Add(new RecordedSegment(segment.Content, segment.IsFirstSegment, segment.IsLastSegment));
            if (segment.Content == PreExecutionPrompt)
            {
                this.PreExecutionPromptDelivered.TrySetResult(true);
            }
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

        public sealed record RecordedSegment(string Content, bool IsFirst, bool IsLast);
    }
}
