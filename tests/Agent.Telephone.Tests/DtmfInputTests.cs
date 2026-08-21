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
    public async Task DtmfGatedAIFunction_HidesTheInjectedResultAndWaitsBeforeInvokingTheToolAsync()
    {
        var target = new GatedToolTarget();
        var completion = new TaskCompletionSource<DtmfInputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new TestAssistantControl(token => completion.Task.WaitAsync(token));
        AIFunction inner = CreateTargetFunction(target);
        var function = new DtmfGatedAIFunction(inner, control, DtmfKey.One | DtmfKey.Star, "dtmfInput");

        FunctionMetadata metadata = FunctionToolHelper.ToFunctionMetadata(inner);
        Assert.DoesNotContain(metadata.Parameters!, parameter => parameter.Name == "dtmfInput");

        ValueTask<object?> invocation = function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None);
        Assert.False(target.Invoked);
        Assert.Equal(DtmfKey.One | DtmfKey.Star, control.RequestedKeys);

        completion.SetResult(new DtmfInputResult(DtmfInputStatus.Accepted) { SelectedKey = DtmfKey.Star });
        await invocation;

        Assert.True(target.Invoked);
        Assert.Equal(DtmfKey.Star, target.Input!.SelectedKey);
    }

    [Fact]
    public async Task DtmfGatedAIFunction_InjectsTimeoutAndDoesNotRunOnCancellationAsync()
    {
        var timeoutTarget = new GatedToolTarget();
        var timeoutControl = new TestAssistantControl(_ => Task.FromResult(
            new DtmfInputResult(DtmfInputStatus.TimedOut, "超时")));
        var timeoutFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(timeoutTarget),
            timeoutControl,
            DtmfKey.Pound,
            "dtmfInput");

        await timeoutFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            CancellationToken.None);
        Assert.True(timeoutTarget.Invoked);
        Assert.Equal(DtmfInputStatus.TimedOut, timeoutTarget.Input!.Status);

        var cancelledTarget = new GatedToolTarget();
        var cancelledCompletion = new TaskCompletionSource<DtmfInputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledControl = new TestAssistantControl(token => cancelledCompletion.Task.WaitAsync(token));
        var cancelledFunction = new DtmfGatedAIFunction(
            CreateTargetFunction(cancelledTarget),
            cancelledControl,
            DtmfKey.One,
            "dtmfInput");
        using var cancellation = new CancellationTokenSource();

        ValueTask<object?> invocation = cancelledFunction.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = "北京" }),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invocation);
        Assert.False(cancelledTarget.Invoked);
    }

    [Fact]
    public void FunctionToolManager_RejectsDtmfToolsWithoutTheRequiredSignatureOrIntentMode()
    {
        Assert.False(BuildFunctionTools<MissingDtmfResultTool>("FunctionCall"));
        Assert.False(BuildFunctionTools<ValidDtmfTool>("IntentLlm"));
    }

    [Fact]
    public void FunctionToolManager_ListsEachAcceptedDtmfKeyInTheGeneratedDescription()
    {
        MethodInfo method = typeof(FunctionToolManager).GetMethod(
            "BuildDtmfKeyInstruction",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string instruction = Assert.IsType<string>(method.Invoke(
            null,
            [DtmfKey.Zero | DtmfKey.One | DtmfKey.Two | DtmfKey.Star | DtmfKey.Pound]));

        Assert.Contains("当前函数可接受的 DTMF 按键为：0、1、2、*、#。", instruction);
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

    private sealed class TestAssistantControl : IAssistantControl
    {
        private readonly Func<CancellationToken, Task<DtmfInputResult>> _request;

        public TestAssistantControl(Func<CancellationToken, Task<DtmfInputResult>> request)
        {
            this._request = request;
        }

        public string? CallerNumber => null;
        public string? AssistantNumber => null;
        public bool IsCallActive => true;
        public DtmfKey RequestedKeys { get; private set; }

        public Task<AssistantSwitchResult> SwitchAssistantAsync(
            string targetAssistantNumber,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void HangupCurrentCall()
        {
        }

        public Task<DtmfInputResult> RequestDtmfInputAsync(
            DtmfKey keys,
            CancellationToken cancellationToken = default)
        {
            this.RequestedKeys = keys;
            return this._request(cancellationToken);
        }
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
        [ToolBehavior(DtmfKeys = DtmfKey.One)]
        public FunctionReturn<string> Choose() => new();
    }

    private sealed class ValidDtmfTool : PrivateFunctionTool
    {
        [Description("请选择。")]
        [ToolBehavior(DtmfKeys = DtmfKey.One)]
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
