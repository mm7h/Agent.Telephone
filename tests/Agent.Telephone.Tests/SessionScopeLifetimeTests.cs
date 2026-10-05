using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.FunctionTools;
using Agent.Telephone.Handlers;
using Agent.Telephone.Management;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.CallControl;
using Agent.Telephone.Resources;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class SessionScopeLifetimeTests
{
    [Fact]
    public async Task Close_DisposesEachScopeOnceWithoutDisposingSharedServicesAsync()
    {
        await using ServiceProvider root = new ServiceCollection()
            .AddTransient<DisposableProbe>()
            .AddSingleton<SharedProbe>()
            .BuildServiceProvider();
        using CallFixture first = new(root);
        using CallFixture second = new(root);
        DisposableProbe callResource = first.Call.ServiceProvider.GetRequiredService<DisposableProbe>();
        DisposableProbe agentResource = first.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        DisposableProbe secondResource = second.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        SharedProbe shared = first.Call.AIAgentContext.ServiceProvider.GetRequiredService<SharedProbe>();
        Assert.Same(shared, second.Call.AIAgentContext.ServiceProvider.GetRequiredService<SharedProbe>());
        Assert.NotSame(callResource, agentResource);
        Assert.NotSame(agentResource, secondResource);

        first.Device.CloseCallSession(first.Call);
        first.Call.Dispose();
        await first.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, callResource.DisposeCount);
        Assert.Equal(1, agentResource.DisposeCount);
        Assert.Equal(0, secondResource.DisposeCount);
        Assert.Equal(0, shared.DisposeCount);
        Assert.False(first.Call.TryAcquireUse(out _));
        second.Call.Dispose();
        await second.Call.Disposal;
        await root.DisposeAsync();
        Assert.Equal(1, agentResource.DisposeCount);
        Assert.Equal(1, shared.DisposeCount);
    }

    [Fact]
    public async Task Hangup_PreservesTurnAndScopeUntilBackgroundLeaseEndsAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe resource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        Assert.True(fixture.Call.TryAcquireUse(out IDisposable? lease));
        CancellationToken turnToken = fixture.Call.Token;

        fixture.Device.CloseCallSession(fixture.Call);

        Assert.True(fixture.Call.CallToken.IsCancellationRequested);
        Assert.False(turnToken.IsCancellationRequested);
        Assert.False(fixture.Call.Disposal.IsCompleted);
        Assert.Equal(0, resource.DisposeCount);
        lease!.Dispose();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(turnToken.IsCancellationRequested);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_CancelsDetachedTurnsAndWaitsForTheirLeasesAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe resource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        Assert.True(fixture.Call.TryAcquireUse(out IDisposable? lease));
        fixture.Device.CloseCallSession(fixture.Call);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        Task shutdown = fixture.Device.StopAsync(timeout.Token);

        Assert.True(fixture.Call.Token.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, resource.DisposeCount);
        lease!.Dispose();
        await shutdown;
        await fixture.Device.StopAsync(timeout.Token);
        Assert.Equal(1, resource.DisposeCount);
        Assert.False(fixture.Device.TryBeginCallback());
    }

    [Fact]
    public async Task Switch_ReplacesOnlyAgentScopeAndPreservesCallIdentityAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        ActiveCallContext call = fixture.Call;
        AIAgentContext previousAgent = call.AIAgentContext;
        DisposableProbe previousResource = previousAgent.ServiceProvider.GetRequiredService<DisposableProbe>();
        DisposableProbe callResource = call.ServiceProvider.GetRequiredService<DisposableProbe>();
        SIPUserAgent userAgent = call.UserAgent;
        VoIPMediaSession mediaSession = call.VoIPRTP;
        string callId = call.CallId;
        CancellationToken callToken = call.CallToken;

        Assert.True(call.TryBeginAssistantSwitch());
        Assert.False(call.TryBeginAssistantSwitch());
        Assert.True(await call.TryReplaceAssistantSessionAsync("2001"));
        call.CompleteAssistantSwitch();
        DisposableProbe nextResource = call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();

        Assert.Equal(1, previousResource.DisposeCount);
        Assert.Equal(0, callResource.DisposeCount);
        Assert.NotSame(previousAgent, call.AIAgentContext);
        Assert.NotSame(previousResource, nextResource);
        Assert.Same(userAgent, call.UserAgent);
        Assert.Same(mediaSession, call.VoIPRTP);
        Assert.Equal(callId, call.CallId);
        Assert.Equal(callToken, call.CallToken);
        Assert.Equal("2001", call.DialedNumber);
        call.Dispose();
        await call.Disposal;
        Assert.Equal(1, nextResource.DisposeCount);
        Assert.Equal(1, callResource.DisposeCount);
    }

    [Fact]
    public async Task Close_WaitsForConsumerBeforeDisposingHandlerAndProviderDependenciesAsync()
    {
        await using ServiceProvider root = new ServiceCollection()
            .AddTransient<DisposableProbe>()
            .AddTransient<TrackingHandler>()
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        TrackingHandler handler = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<TrackingHandler>();
        handler.ActiveCallContext = fixture.Call;
        Assert.True(handler.Build());
        TaskCompletionSource consumer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Call.AIAgentContext.HandlerPipeline.InitHandlerPipeline(
            [handler],
            [],
            [consumer.Task],
            NullLogger.Instance);

        fixture.Call.Dispose();
        Assert.False(fixture.Call.Disposal.IsCompleted);
        Assert.Equal(0, handler.DisposeCount);
        Assert.Equal(0, handler.Dependency.DisposeCount);
        consumer.SetResult();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.DisposeCount);
        Assert.Equal(1, handler.Dependency.DisposeCount);
        Assert.Equal(0, handler.DependencyDisposeCountAtCleanup);
        int notifications = handler.Notifications;
        fixture.Call.RestartTurn();
        handler.Dispose();
        Assert.Equal(notifications, handler.Notifications);
        Assert.Equal(1, handler.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_HangupWaitsForRollbackAndToolHooksBeforeScopeDisposalAsync(bool failInitialization)
    {
        ToolState state = new() { FailInitialization = failInitialization };
        await using ServiceProvider root = new ServiceCollection()
            .AddSingleton(state)
            .AddTransient<IPrivateFunctionTool, DelayedTool>()
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        FunctionToolManager manager = new(
            NullLoggerFactory.Instance,
            root,
            fixture.Config,
            NullLogger<FunctionToolManager>.Instance);
        Assert.True(manager.BuildComponent());
        Task<bool> initialization = manager.BuildForActiveCallAsync(fixture.Device);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Device.CloseCallSession(fixture.Call);
        Assert.False(fixture.Call.Disposal.IsCompleted);
        Assert.Equal(["initialize"], state.Events);
        state.Continue.SetResult();
        if (failInitialization)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => initialization);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => initialization);
        }
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["initialize", "closed", "released", "dispose"], state.Events);
    }

    [Fact]
    public async Task TurnRestart_RacingHandlerDisposalDoesNotReviveCallbacksAsync()
    {
        await using ServiceProvider root = new ServiceCollection()
            .AddTransient<DisposableProbe>()
            .AddTransient<TrackingHandler>()
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        TrackingHandler handler = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<TrackingHandler>();
        handler.ActiveCallContext = fixture.Call;
        handler.Build();
        using ManualResetEventSlim callbackStarted = new();
        using ManualResetEventSlim continueCallback = new();
        handler.OnNotification = () =>
        {
            callbackStarted.Set();
            Assert.True(continueCallback.Wait(TimeSpan.FromSeconds(5)));
        };
        Task restart = Task.Run(fixture.Call.RestartTurn);
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)));
        Task dispose = Task.Run(handler.Dispose);
        continueCallback.Set();
        await Task.WhenAll(restart, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        int notifications = handler.Notifications;

        fixture.Call.RestartTurn();
        handler.Dispose();
        Assert.Equal(notifications, handler.Notifications);
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentRestarts_IgnoreLateNotificationsFromAnOlderTurnAsync()
    {
        await using ServiceProvider root = new ServiceCollection()
            .AddTransient<DisposableProbe>()
            .AddTransient<TrackingHandler>()
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        TrackingHandler handler = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<TrackingHandler>();
        handler.ActiveCallContext = fixture.Call;
        handler.Build();
        using ManualResetEventSlim cancellationStarted = new();
        using ManualResetEventSlim continueCancellation = new();
        using CancellationTokenRegistration registration = fixture.Call.Token.Register(() =>
        {
            cancellationStarted.Set();
            Assert.True(continueCancellation.Wait(TimeSpan.FromSeconds(5)));
        });

        Task firstRestart = Task.Run(fixture.Call.RestartTurn);
        Assert.True(cancellationStarted.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(fixture.Call.RestartTurn).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            continueCancellation.Set();
        }
        await firstRestart.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(handler.CurrentToken.IsCancellationRequested);
        Assert.Equal(2, fixture.Call.TurnId);
    }

    [Fact]
    public async Task ShutdownDeadline_StopsWaitingWithoutDisposingResourcesStillInUseAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe resource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        Assert.True(fixture.Call.TryAcquireUse(out IDisposable? lease));
        using CancellationTokenSource deadline = new();
        Task shutdown = fixture.Device.StopAsync(deadline.Token);
        deadline.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown);
        Assert.Equal(0, resource.DisposeCount);
        lease!.Dispose();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task LastLeaseReleasedByHangupCallback_DoesNotDisposeCancellationSourceMidCloseAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe resource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        Assert.True(fixture.Call.TryAcquireUse(out IDisposable? lease));
        using CancellationTokenRegistration registration = fixture.Call.CallToken.Register(() =>
        {
            lease!.Dispose();
            Assert.False(fixture.Call.Disposal.IsCompleted);
            Assert.Equal(0, resource.DisposeCount);
        });

        fixture.Call.Dispose();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task TurnCancellationCallbackFailure_DoesNotSkipScopeCleanupAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe resource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        using CancellationTokenRegistration registration = fixture.Call.Token.Register(
            () => throw new InvalidOperationException("Cancellation failed."));

        fixture.Call.Dispose();
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Call.Disposal);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolClose_AwaitsBothHooksBeforeScopeDisposalEvenWhenOneHookFailsAsync(bool failClosing)
    {
        ToolState state = new() { FailClosing = failClosing };
        state.Continue.SetResult();
        await using ServiceProvider root = new ServiceCollection()
            .AddSingleton(state)
            .AddTransient<IPrivateFunctionTool, DelayedTool>()
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        FunctionToolManager manager = new(
            NullLoggerFactory.Instance,
            root,
            fixture.Config,
            NullLogger<FunctionToolManager>.Instance);
        Assert.True(manager.BuildComponent());
        Assert.True(await manager.BuildForActiveCallAsync(fixture.Device));

        fixture.Call.Dispose();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["initialize", "closed", "released", "dispose"], state.Events);
    }

    [Fact]
    public async Task ProviderConstructionFailure_ReleasesDependenciesThroughAgentScopeAsync()
    {
        DisposableProbe? dependency = null;
        await using ServiceProvider root = new ServiceCollection()
            .AddTransient<DisposableProbe>()
            .AddTransient<IAudioProcessor>(services =>
            {
                dependency = services.GetRequiredService<DisposableProbe>();
                throw new InvalidOperationException("Provider construction failed.");
            })
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        ProviderManager manager = new(root, fixture.Config, NullLogger<ProviderManager>.Instance);

        Assert.False(await manager.BuildForActiveCallAsync(fixture.Device));
        Assert.NotNull(dependency);
        Assert.Equal(0, dependency.DisposeCount);
        fixture.Call.Dispose();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, dependency.DisposeCount);
    }

    [Fact]
    public void ResourceManagerDisposal_DoesNotResolveServicesFromDisposedContainer()
    {
        ServiceProvider root = new ServiceCollection().BuildServiceProvider();
        ResourceManager manager = new(root, new TelephoneConfig(), NullLogger<ResourceManager>.Instance);
        root.Dispose();

        manager.Dispose();
    }

    [Fact]
    public async Task GlobalTools_StopAwaitsReleaseHookBeforeContainerDisposalAsync()
    {
        GlobalToolState state = new();
        await using ServiceProvider root = new ServiceCollection()
            .AddSingleton(state)
            .AddSingleton<IFunctionTool, GlobalTool>()
            .AddSingleton(services => new FunctionToolManager(
                NullLoggerFactory.Instance,
                services,
                new TelephoneConfig(),
                NullLogger<FunctionToolManager>.Instance))
            .BuildServiceProvider();
        FunctionToolManager manager = root.GetRequiredService<FunctionToolManager>();
        Assert.True(manager.BuildComponent());

        Task stop = manager.StopAsync();
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(stop, manager.StopAsync());
        Assert.False(stop.IsCompleted);
        Assert.Equal(["release-start"], state.Events);
        state.Continue.SetResult();
        await stop;
        await root.DisposeAsync();
        Assert.Equal(["release-start", "release-end", "dispose"], state.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switch_StopsRingbackAndCleansNewScopeOnInitializationFailureOrHangupAsync(bool hangup)
    {
        ToolState state = new() { FailInitialization = !hangup };
        await using ServiceProvider root = new ServiceCollection()
            .AddSingleton(state)
            .AddTransient<IPrivateFunctionTool, DelayedTool>()
            .AddTransient<DisposableProbe>()
            .BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe previousResource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        FunctionToolManager tools = new(
            NullLoggerFactory.Instance,
            root,
            fixture.Config,
            NullLogger<FunctionToolManager>.Instance);
        Assert.True(tools.BuildComponent());
        fixture.Config.AssistantConfigs[1].AllowedTools = [nameof(DelayedTool.Execute)];
        RingbackPlayer player = new();
        AssistantRoleControl controller = new(
            tools,
            new ProviderManager(root, fixture.Config, NullLogger<ProviderManager>.Instance),
            new HandlerManager(root, fixture.Config, NullLogger<HandlerManager>.Instance),
            player,
            NullLogger<AssistantRoleControl>.Instance);
        Assert.True(fixture.Call.TryBeginAssistantSwitch());
        Assert.True(fixture.Call.TryAcquireUse(out IDisposable? lease));
        using IDisposable callLease = lease!;
        MethodInfo switchCore = typeof(AssistantRoleControl).GetMethod(
            "SwitchAssistantCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<bool> switching = (Task<bool>)switchCore.Invoke(controller, [fixture.Call, "2001"])!;
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, previousResource.DisposeCount);
        Assert.True(fixture.Call.IsAgentMediaPaused);
        Assert.False(player.Stopped);
        if (hangup)
        {
            fixture.Call.Dispose();
            Assert.False(fixture.Call.Disposal.IsCompleted);
        }
        state.Continue.SetResult();

        Assert.False(await switching.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(player.Stopped);
        Assert.False(fixture.Call.IsAgentSwitching);
        Assert.Equal(["initialize", "closed", "released"], state.Events);
        fixture.Call.Dispose();
        lease!.Dispose();
        await fixture.Call.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["initialize", "closed", "released", "dispose"], state.Events);
    }

    [Fact]
    public async Task Shutdown_CallbackFailureStillWaitsForLeasedScopeCleanupAsync()
    {
        await using ServiceProvider root = new ServiceCollection().AddTransient<DisposableProbe>().BuildServiceProvider();
        using CallFixture fixture = new(root);
        DisposableProbe resource = fixture.Call.AIAgentContext.ServiceProvider.GetRequiredService<DisposableProbe>();
        Assert.True(fixture.Call.TryAcquireUse(out IDisposable? lease));
        using IDisposable callLease = lease!;
        using CancellationTokenRegistration registration = fixture.Call.CallToken.Register(
            () => throw new InvalidOperationException("Hangup callback failed."));
        Task shutdown = fixture.Device.StopAsync(CancellationToken.None);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, resource.DisposeCount);
        lease!.Dispose();

        await Assert.ThrowsAsync<AggregateException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(fixture.Call.Disposal.IsCompletedSuccessfully);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task Switch_UsesNormalizedTargetForResultAndSessionReplacementAsync()
    {
        await using ServiceProvider root = new ServiceCollection().BuildServiceProvider();
        using CallFixture fixture = new(root);
        using SIPTransport phoneTransport = new();
        SIPUDPChannel phoneChannel = new(System.Net.IPAddress.Loopback, 0);
        phoneTransport.AddSIPChannel(phoneChannel);
        SIPUserAgent phone = new(phoneTransport, null, false);
        AudioExtrasSource phoneSource = new(new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat), new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        using VoIPMediaSession phoneMedia = new(new MediaEndPoints { AudioSource = phoneSource });
        phoneTransport.SIPTransportRequestReceived += OnRequestAsync;
        try
        {
            Assert.True(await fixture.Call.UserAgent.Call(
                $"sip:1001@{phoneChannel.ListeningSIPEndPoint.GetIPEndPoint()}",
                "2000",
                string.Empty,
                fixture.Call.VoIPRTP,
                5));
            FunctionToolManager tools = new(NullLoggerFactory.Instance, root, fixture.Config, NullLogger<FunctionToolManager>.Instance);
            AssistantRoleControl controller = new(
                tools,
                new ProviderManager(root, fixture.Config, NullLogger<ProviderManager>.Instance),
                new HandlerManager(root, fixture.Config, NullLogger<HandlerManager>.Instance),
                new RingbackPlayer(),
                NullLogger<AssistantRoleControl>.Instance);
            Assert.True(controller.Build(fixture.Config.AssistantConfigs));

            AssistantSwitchResult result = await controller.SwitchAssistantAsync(fixture.Call, " 2001 ");

            Assert.Equal(AssistantSwitchStatus.Accepted, result.Status);
            Assert.Equal("2001", result.TargetAssistantNumber);
            Assert.Equal("2001", fixture.Call.DialedNumber);
        }
        finally
        {
            phoneTransport.SIPTransportRequestReceived -= OnRequestAsync;
            fixture.Call.UserAgent.Hangup();
            phone.Hangup();
        }

        async Task OnRequestAsync(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
        {
            if (request.Method == SIPMethodsEnum.INVITE)
            {
                Assert.True(await phone.Answer(phone.AcceptCall(request), phoneMedia));
            }
        }
    }

    private sealed class CallFixture : IDisposable
    {
        private readonly SIPTransport _transport = new();

        public CallFixture(IServiceProvider root)
        {
            this.Config = new TelephoneConfig
            {
                AssistantConfigs =
                [
                    new AssistantConfig { DialingNumber = "2000", AllowedTools = [nameof(DelayedTool.Execute)] },
                    new AssistantConfig { DialingNumber = "2001" },
                ],
            };
            this.Device = new DeviceContext(
                root.GetRequiredService<IServiceScopeFactory>(),
                this._transport,
                new DeviceRegistrationRecord(
                    "1001",
                    "sip:1001@device.test",
                    "sip:1001@192.0.2.10:5060",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(5)),
                this.Config.AssistantConfigs);
            Assert.True(this.Device.TryBeginCallback());
            AudioExtrasSource source = new(
                new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
            VoIPMediaSession media = new(new MediaEndPoints { AudioSource = source });
            SIPUserAgent userAgent = new(this._transport, null, false);
            Assert.True(this.Device.TryAttachCallbackCallSession(
                "sip:1001@device.test",
                "2000",
                userAgent,
                media,
                out ActiveCallContext? call));
            this.Call = call!;
            this.Device.EndCallback();
        }

        public TelephoneConfig Config { get; }
        public DeviceContext Device { get; }
        public ActiveCallContext Call { get; }

        public void Dispose()
        {
            this.Device.Dispose();
            this.Call.Dispose();
            this._transport.Shutdown();
        }
    }

    private class DisposableProbe : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => this.DisposeCount++;
    }

    private sealed class SharedProbe : DisposableProbe
    {
    }

    private sealed class TrackingHandler : BaseHandler
    {
        public TrackingHandler(DisposableProbe dependency)
            : base(new TelephoneConfig(), NullLogger.Instance)
        {
            this.Dependency = dependency;
        }

        public override string HandlerName => nameof(TrackingHandler);
        public DisposableProbe Dependency { get; }
        public int DisposeCount { get; private set; }
        public int DependencyDisposeCountAtCleanup { get; private set; }
        public int Notifications { get; private set; }
        public CancellationToken CurrentToken => this.HandlerToken;
        public Action? OnNotification { get; set; }

        public override bool Build()
        {
            this.RegisterCancellationToken(this.ActiveCallContext);
            return true;
        }

        protected override void OnHandlerTokenChanged()
        {
            this.Notifications++;
            this.OnNotification?.Invoke();
        }

        protected override void DisposeResources()
        {
            this.DependencyDisposeCountAtCleanup = this.Dependency.DisposeCount;
            this.DisposeCount++;
        }
    }

    private sealed class RingbackPlayer : IAudioPromptPlayer
    {
        public bool Stopped { get; private set; }

        public async Task<bool> PlaySIPCodeAudioLoopAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SIPResponseStatusCodesEnum.Ringing, sipCode);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            this.Stopped = true;
            return true;
        }

        public Task<bool> PlaySIPCodeAudioAsync(
            SIPResponseStatusCodesEnum sipCode,
            VoIPMediaSession mediaSession,
            AudioFormat audioFormat,
            int packetTimeMs,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> PlayCachedAudioFilesAsync(
            IReadOnlyList<string> relativeFilePaths,
            int outputSampleRate,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> PlayFileAsync(
            string filePath,
            int outputSampleRate,
            int packetTimeMs,
            Action<float[]> onAudioData,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class GlobalToolState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
    }

    private sealed class GlobalTool(GlobalToolState state) : FunctionTool, IDisposable
    {
        public string Execute() => "done";

        public override async ValueTask OnFunctionToolReleasedAsync()
        {
            state.Events.Add("release-start");
            state.Started.SetResult();
            await state.Continue.Task;
            state.Events.Add("release-end");
        }

        public void Dispose() => state.Events.Add("dispose");
    }

    private sealed class ToolState
    {
        public bool FailInitialization { get; init; }
        public bool FailClosing { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
    }

    private sealed class DelayedTool(ToolState state) : PrivateFunctionTool, IDisposable
    {
        private bool _initialized;

        public string Execute() => "done";

        public override async ValueTask OnFunctionToolInitializedAsync()
        {
            this._initialized = true;
            state.Events.Add("initialize");
            state.Started.SetResult();
            await state.Continue.Task;
            if (state.FailInitialization)
            {
                throw new InvalidOperationException("Initialization failed.");
            }
        }

        public override ValueTask OnDeviceClosedAsync()
        {
            state.Events.Add("closed");
            if (state.FailClosing)
            {
                throw new InvalidOperationException("Closing failed.");
            }
            return ValueTask.CompletedTask;
        }

        public override ValueTask OnFunctionToolReleasedAsync()
        {
            state.Events.Add("released");
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (this._initialized)
            {
                state.Events.Add("dispose");
            }
        }
    }
}
