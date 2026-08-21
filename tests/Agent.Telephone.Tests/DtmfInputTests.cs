using System.ComponentModel;
using System.Reflection;
using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.FunctionTools;
using Agent.Telephone.Helpers;
using Agent.Telephone.Management;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.Dtmf;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class DtmfInputTests
{
    [Theory]
    [InlineData(0, DtmfKey.Zero)]
    [InlineData(1, DtmfKey.One)]
    [InlineData(2, DtmfKey.Two)]
    [InlineData(3, DtmfKey.Three)]
    [InlineData(4, DtmfKey.Four)]
    [InlineData(5, DtmfKey.Five)]
    [InlineData(6, DtmfKey.Six)]
    [InlineData(7, DtmfKey.Seven)]
    [InlineData(8, DtmfKey.Eight)]
    [InlineData(9, DtmfKey.Nine)]
    [InlineData(10, DtmfKey.Star)]
    [InlineData(11, DtmfKey.Pound)]
    public async Task RequestDtmfInputAsync_WaitsAndReturnsTheSelectedTelephoneKeyAsync(int tone, DtmfKey expected)
    {
        using TestCallSession session = CreateCall();
        using var input = new DefaultDtmfInput(NullLogger<DefaultDtmfInput>.Instance);
        AddDtmfRegistration(session.Call, expected);

        Task<DtmfInputResult> request = input.RequestDtmfInputAsync(session.Call, expected, CancellationToken.None);

        Assert.False(request.IsCompleted);
        input.HandleDtmfTone(session.Call, (byte)tone);

        DtmfInputResult result = await request.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DtmfInputStatus.Accepted, result.Status);
        Assert.Equal(expected, result.SelectedKey);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RequestDtmfInputAsync_IgnoresKeysOutsideTheCurrentMenuAsync()
    {
        using TestCallSession session = CreateCall();
        using var input = new DefaultDtmfInput(NullLogger<DefaultDtmfInput>.Instance);
        AddDtmfRegistration(session.Call, DtmfKey.One);

        Task<DtmfInputResult> request = input.RequestDtmfInputAsync(session.Call, DtmfKey.One, CancellationToken.None);
        input.HandleDtmfTone(session.Call, 2);

        Assert.False(request.IsCompleted);
        input.HandleDtmfTone(session.Call, 1);

        DtmfInputResult result = await request.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DtmfKey.One, result.SelectedKey);
    }

    [Fact]
    public async Task RequestDtmfInputAsync_RejectsConcurrentMenuWithoutInterruptingTheFirstAsync()
    {
        using TestCallSession session = CreateCall();
        using var input = new DefaultDtmfInput(NullLogger<DefaultDtmfInput>.Instance);
        AddDtmfRegistration(session.Call, DtmfKey.One | DtmfKey.Two);

        Task<DtmfInputResult> first = input.RequestDtmfInputAsync(session.Call, DtmfKey.One, CancellationToken.None);
        DtmfInputResult second = await input.RequestDtmfInputAsync(session.Call, DtmfKey.Two, CancellationToken.None);

        Assert.Equal(DtmfInputStatus.AlreadyWaiting, second.Status);
        input.HandleDtmfTone(session.Call, 1);
        Assert.Equal(DtmfKey.One, (await first.WaitAsync(TimeSpan.FromSeconds(1))).SelectedKey);
    }

    [Fact]
    public async Task RequestDtmfInputAsync_CancelsWhenTheTurnIsReplacedAsync()
    {
        using TestCallSession session = CreateCall();
        using var input = new DefaultDtmfInput(NullLogger<DefaultDtmfInput>.Instance);
        AddDtmfRegistration(session.Call, DtmfKey.One);

        Task<DtmfInputResult> request = input.RequestDtmfInputAsync(session.Call, DtmfKey.One, CancellationToken.None);
        session.Call.RestartTurn();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    [Fact]
    public async Task RequestDtmfInputAsync_ReturnsTimedOutAfterTheFixedTimeoutAsync()
    {
        using TestCallSession session = CreateCall();
        using var input = new DefaultDtmfInput(NullLogger<DefaultDtmfInput>.Instance);
        AddDtmfRegistration(session.Call, DtmfKey.One);

        DtmfInputResult result = await input.RequestDtmfInputAsync(
            session.Call,
            DtmfKey.One,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(16));

        Assert.Equal(DtmfInputStatus.TimedOut, result.Status);
        Assert.Null(result.SelectedKey);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task WaitForDtmfKeyAsync_ReturnsNullForOfflineMenuTimeoutAsync()
    {
        using TestCallSession session = CreateCall();
        using var input = new DefaultDtmfInput(NullLogger<DefaultDtmfInput>.Instance);

        DtmfKey? selected = await input.WaitForDtmfKeyAsync(
            session.Call,
            DtmfKey.Star | DtmfKey.Pound,
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.Null(selected);
    }

    [Fact]
    public async Task DtmfGatedAIFunction_PlaysPromptMutesMicrophoneAndWaitsBeforeInvokingTheToolAsync()
    {
        using TestCallSession session = CreateCall();
        var target = new GatedToolTarget();
        var input = new BlockingDtmfInput();
        session.Call.AIAgentContext.PrivateProvider.SetDtmfInput(input);
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Call.AIAgentContext.SetPromptSynthesizer((content, paragraphId, sentenceId, cancellationToken) =>
        {
            Assert.Equal("确认查询请按1。", content);
            Assert.True(session.Call.IsUserAudioInputPaused);
            promptStarted.TrySetResult();
            return Task.FromResult(true);
        });
        AIFunction inner = CreateTargetFunction(target);
        var function = new DtmfGatedAIFunction(
            inner,
            session.Call.AIAgentContext,
            DtmfKey.One,
            "确认查询请按1。",
            "dtmfInput");

        FunctionMetadata metadata = FunctionToolHelper.ToFunctionMetadata(inner);
        Assert.DoesNotContain(metadata.Parameters!, parameter => parameter.Name == "dtmfInput");

        Task<object?> invocation = function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None).AsTask();
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(target.Invoked);
        Assert.True(session.Call.IsUserAudioInputPaused);
        Assert.False(input.RequestStarted.Task.IsCompleted);

        session.Call.CompletePromptPlayback(fullyPlayed: true);
        await input.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(session.Call.IsUserAudioInputPaused);

        input.Complete(new DtmfInputResult(DtmfInputStatus.Accepted) { SelectedKey = DtmfKey.One });
        await invocation;

        Assert.True(target.Invoked);
        Assert.Equal(DtmfKey.One, target.Input!.SelectedKey);
        Assert.False(session.Call.IsUserAudioInputPaused);
    }

    [Fact]
    public async Task DtmfGatedAIFunction_RejectsConcurrentMenusWithoutPlayingAnotherPromptAsync()
    {
        using TestCallSession session = CreateCall();
        var input = new BlockingDtmfInput();
        session.Call.AIAgentContext.PrivateProvider.SetDtmfInput(input);
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int promptCount = 0;
        session.Call.AIAgentContext.SetPromptSynthesizer((content, paragraphId, sentenceId, cancellationToken) =>
        {
            Interlocked.Increment(ref promptCount);
            promptStarted.TrySetResult();
            return Task.FromResult(true);
        });

        var firstTarget = new GatedToolTarget();
        var firstFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(firstTarget),
            session.Call.AIAgentContext,
            DtmfKey.One,
            "确认请按1。",
            "dtmfInput");
        Task<object?> firstInvocation = firstFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None).AsTask();
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondTarget = new GatedToolTarget();
        var secondFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(secondTarget),
            session.Call.AIAgentContext,
            DtmfKey.Two,
            "取消请按2。",
            "dtmfInput");
        await secondFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None);

        Assert.Equal(1, promptCount);
        Assert.Equal(DtmfInputStatus.AlreadyWaiting, secondTarget.Input!.Status);
        Assert.True(session.Call.IsUserAudioInputPaused);

        session.Call.CompletePromptPlayback(fullyPlayed: true);
        await input.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        input.Complete(new DtmfInputResult(DtmfInputStatus.Accepted) { SelectedKey = DtmfKey.One });
        await firstInvocation;

        Assert.True(firstTarget.Invoked);
        Assert.False(session.Call.IsUserAudioInputPaused);
    }

    [Fact]
    public async Task RequestDtmfInteractionAsync_PreservesAnExistingMicrophonePauseAsync()
    {
        using TestCallSession session = CreateCall();
        var input = new BlockingDtmfInput();
        session.Call.AIAgentContext.PrivateProvider.SetDtmfInput(input);
        session.Call.PauseUserAudioInput();
        session.Call.AIAgentContext.SetPromptSynthesizer((content, paragraphId, sentenceId, cancellationToken) =>
        {
            session.Call.CompletePromptPlayback(fullyPlayed: true);
            return Task.FromResult(true);
        });

        Task<DtmfInputResult> interaction = session.Call.AIAgentContext.RequestDtmfInteractionAsync(
            "确认请按1。",
            DtmfKey.One,
            CancellationToken.None);
        await input.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        input.Complete(new DtmfInputResult(DtmfInputStatus.Accepted) { SelectedKey = DtmfKey.One });

        Assert.Equal(DtmfKey.One, (await interaction).SelectedKey);
        Assert.True(session.Call.IsUserAudioInputPaused);
    }

    [Fact]
    public async Task DtmfGatedAIFunction_HandlesUnavailableTimeoutAndCancellationAsync()
    {
        using TestCallSession unavailableSession = CreateCall();
        var unavailableTarget = new GatedToolTarget();
        var unavailableInput = new BlockingDtmfInput();
        unavailableSession.Call.AIAgentContext.PrivateProvider.SetDtmfInput(unavailableInput);
        unavailableSession.Call.AIAgentContext.SetPromptSynthesizer((content, paragraphId, sentenceId, cancellationToken) =>
            Task.FromResult(false));
        var unavailableFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(unavailableTarget),
            unavailableSession.Call.AIAgentContext,
            DtmfKey.Pound,
            "结束请按井号键。",
            "dtmfInput");

        await unavailableFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None);
        Assert.True(unavailableTarget.Invoked);
        Assert.Equal(DtmfInputStatus.Unavailable, unavailableTarget.Input!.Status);
        Assert.False(unavailableInput.RequestStarted.Task.IsCompleted);
        Assert.False(unavailableSession.Call.IsUserAudioInputPaused);

        using TestCallSession timeoutSession = CreateCall();
        var timeoutTarget = new GatedToolTarget();
        var timeoutInput = new BlockingDtmfInput();
        timeoutSession.Call.AIAgentContext.PrivateProvider.SetDtmfInput(timeoutInput);
        timeoutSession.Call.AIAgentContext.SetPromptSynthesizer((content, paragraphId, sentenceId, cancellationToken) =>
        {
            timeoutSession.Call.CompletePromptPlayback(fullyPlayed: true);
            return Task.FromResult(true);
        });
        var timeoutFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(timeoutTarget),
            timeoutSession.Call.AIAgentContext,
            DtmfKey.One,
            "确认请按1。",
            "dtmfInput");

        Task<object?> timeoutInvocation = timeoutFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None).AsTask();
        await timeoutInput.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        timeoutInput.Complete(new DtmfInputResult(DtmfInputStatus.TimedOut, "超时"));
        await timeoutInvocation;

        Assert.True(timeoutTarget.Invoked);
        Assert.Equal(DtmfInputStatus.TimedOut, timeoutTarget.Input!.Status);
        Assert.False(timeoutSession.Call.IsUserAudioInputPaused);

        using TestCallSession cancelledSession = CreateCall();
        var cancelledTarget = new GatedToolTarget();
        cancelledSession.Call.AIAgentContext.PrivateProvider.SetDtmfInput(new BlockingDtmfInput());
        cancelledSession.Call.AIAgentContext.SetPromptSynthesizer((content, paragraphId, sentenceId, cancellationToken) =>
            Task.FromResult(true));
        var cancelledFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(cancelledTarget),
            cancelledSession.Call.AIAgentContext,
            DtmfKey.One,
            "确认请按1。",
            "dtmfInput");
        using var cancellation = new CancellationTokenSource();

        Task<object?> invocation = cancelledFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invocation);
        Assert.False(cancelledTarget.Invoked);
        Assert.False(cancelledSession.Call.IsUserAudioInputPaused);
    }

    [Fact]
    public void FunctionToolManager_RejectsDtmfToolsWithoutPromptRequiredSignatureOrIntentMode()
    {
        Assert.False(BuildFunctionTools<MissingDtmfResultTool>("FunctionCall"));
        Assert.False(BuildFunctionTools<MissingDtmfPromptTool>("FunctionCall"));
        Assert.False(BuildFunctionTools<UnexpectedDtmfPromptTool>("FunctionCall"));
        Assert.False(BuildFunctionTools<ValidDtmfTool>("IntentLlm"));
        Assert.True(BuildFunctionTools<ValidDtmfTool>("FunctionCall"));
    }

    [Fact]
    public void FunctionToolManager_TellsLlmThatSystemManagesTheDtmfPrompt()
    {
        MethodInfo method = typeof(FunctionToolManager).GetMethod(
            "BuildDtmfToolInstruction",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string instruction = Assert.IsType<string>(method.Invoke(null, null));

        Assert.Contains("系统会在调用后自动播放按键提示", instruction);
        Assert.Contains("不要自行播报菜单", instruction);
    }

    private static AIFunction CreateTargetFunction(GatedToolTarget target)
    {
        MethodInfo method = typeof(GatedToolTarget).GetMethod(nameof(GatedToolTarget.Execute))!;
        return AIFunctionFactory.Create(method, target, new AIFunctionFactoryOptions
        {
            ConfigureParameterBinding = parameter => parameter.ParameterType == typeof(DtmfInputResult)
                ? new AIFunctionFactoryOptions.ParameterBindingOptions { ExcludeFromSchema = true }
                : default,
        });
    }

    private static bool BuildFunctionTools<TTool>(string intentType)
        where TTool : class, IPrivateFunctionTool
    {
        ServiceProvider services = new ServiceCollection()
            .AddTransient<IPrivateFunctionTool, TTool>()
            .BuildServiceProvider();
        using (services)
        {
            TelephoneConfig config = new()
            {
                SIPConfig = new SIPConfig(),
                ModelConfig = new ModelConfig
                {
                    ConfiguredSettings = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
                    {
                        ["Intent"] = new Dictionary<string, Dictionary<string, string>>
                        {
                            [intentType] = new Dictionary<string, string> { ["Type"] = intentType },
                        },
                    },
                },
                AssistantConfigs =
                [
                    new AssistantConfig
                    {
                        DialingNumber = "10000",
                        Intent = intentType,
                        AllowedTools = ["Choose"],
                    },
                ],
            };
            var manager = new FunctionToolManager(
                NullLoggerFactory.Instance,
                services,
                config,
                NullLogger<FunctionToolManager>.Instance);

            return manager.BuildComponent();
        }
    }

    private static void AddDtmfRegistration(ActiveCallContext call, DtmfKey keys)
    {
        AIFunction function = AIFunctionFactory.Create((Func<string>)(static () => "test"));
        call.AIAgentContext.PrivateProvider.AddFunctionToolRegistration(
            new FunctionToolRegistration(function, FunctionToolHelper.ToFunctionMetadata(function), ToolAction.Continue, keys));
    }

    private static TestCallSession CreateCall()
    {
        var transport = new SIPTransport();
        var device = new DeviceContext(
            transport,
            new DeviceRegistrationRecord(
                "1001",
                "sip:1001@device.test",
                "sip:1001@192.0.2.10:5060",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            [new AssistantConfig { DialingNumber = "10000" }]);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var call = new ActiveCallContext(
            device,
            "sip:1001@device.test",
            "10000",
            userAgent,
            mediaSession);
        return new TestCallSession(transport, device, call);
    }

    private sealed class BlockingDtmfInput : BaseProvider<BlockingDtmfInput, ModelSetting>, IDtmfInput
    {
        private readonly TaskCompletionSource<DtmfInputResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingDtmfInput()
            : base(NullLogger<BlockingDtmfInput>.Instance)
        {
        }

        public override string ProviderType => "dtmf-input";
        public override string ModelName => nameof(BlockingDtmfInput);
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool Build(ModelSetting settings) => true;

        public Task<DtmfInputResult> RequestDtmfInputAsync(
            ActiveCallContext call,
            DtmfKey keys,
            CancellationToken cancellationToken)
        {
            this.RequestStarted.TrySetResult();
            return this._completion.Task.WaitAsync(cancellationToken);
        }

        public Task<DtmfKey?> WaitForDtmfKeyAsync(
            ActiveCallContext call,
            DtmfKey keys,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DtmfKey?>(null);
        }

        public void HandleDtmfTone(ActiveCallContext call, byte tone) { }
        public void Complete(DtmfInputResult result) => this._completion.TrySetResult(result);
        public override void Dispose() { }
    }

    private sealed class GatedToolTarget
    {
        public bool Invoked { get; private set; }
        public DtmfInputResult? Input { get; private set; }

        public string Execute(string city, DtmfInputResult dtmfInput)
        {
            this.Invoked = true;
            this.Input = dtmfInput;
            return city;
        }
    }

    private sealed class MissingDtmfResultTool : PrivateFunctionTool
    {
        [Description("请选择。")]
        [ToolBehavior(DtmfKeys = DtmfKey.One, DtmfPrompt = "请选择。")]
        public FunctionReturn<string> Choose() => new();
    }

    private sealed class MissingDtmfPromptTool : PrivateFunctionTool
    {
        [Description("请选择。")]
        [ToolBehavior(DtmfKeys = DtmfKey.One)]
        public FunctionReturn<string> Choose(DtmfInputResult dtmfInput) => new();
    }

    private sealed class UnexpectedDtmfPromptTool : PrivateFunctionTool
    {
        [Description("请选择。")]
        [ToolBehavior(DtmfPrompt = "请选择。")]
        public FunctionReturn<string> Choose() => new();
    }

    private sealed class ValidDtmfTool : PrivateFunctionTool
    {
        [Description("请选择。")]
        [ToolBehavior(DtmfKeys = DtmfKey.One, DtmfPrompt = "请选择。")]
        public FunctionReturn<string> Choose(DtmfInputResult dtmfInput) => new();
    }

    private sealed class TestCallSession : IDisposable
    {
        private readonly SIPTransport _transport;
        private readonly DeviceContext _device;

        public TestCallSession(SIPTransport transport, DeviceContext device, ActiveCallContext call)
        {
            this._transport = transport;
            this._device = device;
            this.Call = call;
        }

        public ActiveCallContext Call { get; }

        public void Dispose()
        {
            this.Call.Dispose();
            this._device.Dispose();
            this._transport.Dispose();
        }
    }
}
