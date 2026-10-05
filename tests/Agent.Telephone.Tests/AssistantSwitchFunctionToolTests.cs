using System.Reflection;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.FunctionTools.Adapters;
using Agent.Telephone.Management;
using Agent.Telephone.Sample.Server.FunctionTools;
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

public sealed class AssistantSwitchFunctionToolTests
{
    [Fact]
    public void AssistantControlAdapter_ExposesStableUserAor()
    {
        using TestCallSession session = CreateCall(CreateConfig(), "10000");
        IAssistantControl control = new AssistantControlAdapter(session.Call);
        Assert.Equal("sip:1001@device.test", control.UserAor);
        Assert.Equal("10000", control.AssistantNumber);
    }

    [Fact]
    public async Task BuildForActiveCallAsync_RegistersOnlyTheToolsAllowedForTheAssistantAsync()
    {
        TelephoneConfig config = CreateConfig();
        using ServiceProvider services = new ServiceCollection()
            .AddTransient<IPrivateFunctionTool, AssistantSwitch>()
            .BuildServiceProvider();
        var manager = new FunctionToolManager(
            NullLoggerFactory.Instance,
            services,
            config,
            NullLogger<FunctionToolManager>.Instance);
        Assert.True(manager.BuildComponent());

        using TestCallSession operatorCall = CreateCall(config, "10000", services.GetRequiredService<IServiceScopeFactory>());
        Assert.True(await manager.BuildForActiveCallAsync(operatorCall.Device));
        Assert.Collection(
            operatorCall.Call.AIAgentContext.PrivateProvider.FunctionTools,
            tool => Assert.Equal(nameof(AssistantSwitch.SwitchAssistantAsync), tool.Name));

        using TestCallSession generalCall = CreateCall(config, "10086", services.GetRequiredService<IServiceScopeFactory>());
        Assert.True(await manager.BuildForActiveCallAsync(generalCall.Device));
        Assert.Empty(generalCall.Call.AIAgentContext.PrivateProvider.FunctionTools);
    }

    [Fact]
    public async Task SwitchAssistantAsync_UsesEmptyTargetWhenTheModelOmitsTheArgumentAsync()
    {
        var callControl = new RecordingAssistantControl();
        var tool = new AssistantSwitch
        {
            Logger = NullLogger.Instance,
            CallControl = callControl,
        };
        MethodInfo method = typeof(AssistantSwitch).GetMethod(nameof(AssistantSwitch.SwitchAssistantAsync))!;
        AIFunction function = AIFunctionFactory.Create(method, tool, new AIFunctionFactoryOptions());

        await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Equal(string.Empty, callControl.TargetAssistantNumber);
    }

    private sealed class RecordingAssistantControl : IAssistantControl
    {
        public string? CallerNumber => null;
        public string? AssistantNumber => "10000";
        public bool IsCallActive => true;
        public string? TargetAssistantNumber { get; private set; }

        public Task<AssistantSwitchResult> SwitchAssistantAsync(string targetAssistantNumber, CancellationToken cancellationToken = default)
        {
            this.TargetAssistantNumber = targetAssistantNumber;
            return Task.FromResult(new AssistantSwitchResult(
                AssistantSwitchStatus.InvalidTarget,
                targetAssistantNumber,
                "目标 Agent 号码不能为空。"));
        }

        public void HangupCurrentCall()
        {
        }

        public Task<DtmfInputResult> RequestDtmfInputAsync(DtmfKey keys, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private static TelephoneConfig CreateConfig()
    {
        return new TelephoneConfig
        {
            SIPConfig = new SIPConfig(),
            AssistantConfigs =
            [
                new AssistantConfig
                {
                    DialingNumber = "10000",
                    Intent = "FunctionCall",
                    AllowedTools = [nameof(AssistantSwitch.SwitchAssistantAsync)],
                },
                new AssistantConfig
                {
                    DialingNumber = "10086",
                    Intent = "FunctionCall",
                    AllowedTools = [],
                },
            ],
        };
    }

    private static TestCallSession CreateCall(TelephoneConfig config, string assistantNumber, IServiceScopeFactory? scopeFactory = null)
    {
        var transport = new SIPTransport();
        var device = new DeviceContext(
            scopeFactory ?? TestServices.ScopeFactory,
            transport,
            new DeviceRegistrationRecord(
                "1001",
                "sip:1001@device.test",
                "sip:1001@192.0.2.10:5060",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            config.AssistantConfigs);
        Assert.True(device.TryBeginCallback());
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        Assert.True(device.TryAttachCallbackCallSession(
            "sip:1001@device.test",
            assistantNumber,
            userAgent,
            mediaSession,
            out ActiveCallContext? call));
        Assert.NotNull(call);
        device.EndCallback();
        return new TestCallSession(transport, device, call);
    }

    private sealed class TestCallSession : IDisposable
    {
        private readonly SIPTransport _transport;
        private readonly DeviceContext _device;
        private ActiveCallContext? _call;

        public TestCallSession(SIPTransport transport, DeviceContext device, ActiveCallContext call)
        {
            this._transport = transport;
            this._device = device;
            this._call = call;
        }

        public DeviceContext Device => this._device;

        public ActiveCallContext Call => this._call
            ?? throw new ObjectDisposedException(nameof(TestCallSession));

        public void Dispose()
        {
            ActiveCallContext? call = Interlocked.Exchange(ref this._call, null);
            if (call is not null)
            {
                this._device.CloseCallSession(call);
            }

            this._device.Dispose();
            this._transport.Dispose();
        }
    }
}
