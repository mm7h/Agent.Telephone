using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers;
using Agent.Telephone.Handlers.AIAdapterHandlers;
using Agent.Telephone.Handlers.SIPHandlers;
using Agent.Telephone.Providers.Conversation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using System.Threading.Channels;

namespace Agent.Telephone.Management
{
    internal sealed class HandlerManager : BaseManager
    {
#if DEBUG
        private const int CHANNEL_CAPACITY = 100;
#else
        private const int CHANNEL_CAPACITY = 200;
#endif
        public HandlerManager(IServiceProvider serviceProvider, TelephoneConfig config, ILogger<HandlerManager> logger)
            : base(serviceProvider, config, logger) { }

        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((_, services) =>
            {
                services.AddTransient<ActiveCallHandler>();
                services.AddTransient<RTPHandler>();
                services.AddTransient<AudioReceivedHandler>();
                services.AddTransient<Audio2TextHandler>();
                services.AddTransient<DialogueHandler>();
                services.AddTransient<Text2AudioHandler>();
                services.AddTransient<AudioSendHandler>();
                services.AddSingleton<HandlerManager>();
            });
        }

        public override bool BuildComponent() => true;

        public override Task<bool> OnSIPDeviceRegisteredAsync(
            DeviceContext deviceContext,
            SIPTransport sipTransport,
            SIPRequest sipRequest)
        {
            return this.BuildHandlersAsync(deviceContext, sipRequest);
        }

        public Task<bool> BuildForConnectedCallAsync(DeviceContext deviceContext)
        {
            return this.BuildHandlersAsync(deviceContext, answerRequest: null);
        }

        private async Task<bool> BuildHandlersAsync(
            DeviceContext deviceContext,
            SIPRequest? answerRequest)
        {
            ActiveCallContext? activeCallContext = deviceContext.ActiveCall;
            if (activeCallContext is null)
            {
                this.Logger.LogWarning("设备 {deviceId} 没有活动呼叫，无法构建处理程序管道。", deviceContext.DeviceId);
                return false;
            }

            ActiveCallHandler? activeCallHandler = answerRequest is null
                ? null
                : this.ServiceProvider.GetRequiredService<ActiveCallHandler>();
            var rtp = this.ServiceProvider.GetRequiredService<RTPHandler>();
            var audioReceived = this.ServiceProvider.GetRequiredService<AudioReceivedHandler>();
            var audio2Text = this.ServiceProvider.GetRequiredService<Audio2TextHandler>();
            var dialogue = this.ServiceProvider.GetRequiredService<DialogueHandler>();
            var text2Audio = this.ServiceProvider.GetRequiredService<Text2AudioHandler>();
            var audioSend = this.ServiceProvider.GetRequiredService<AudioSendHandler>();

            IDictionary<string, IHandler> handlerContainer = new Dictionary<string, IHandler>
            {
                [rtp.HandlerName] = rtp,
                [audioReceived.HandlerName] = audioReceived,
                [audio2Text.HandlerName] = audio2Text,
                [dialogue.HandlerName] = dialogue,
                [text2Audio.HandlerName] = text2Audio,
                [audioSend.HandlerName] = audioSend
            };

            HandlerResourcesLifetime? resources = null;
            try
            {
                if (activeCallHandler is not null)
                {
                    this.InitializeDeviceContext(deviceContext, activeCallHandler);
                    if (!activeCallHandler.Build())
                    {
                        this.Logger.LogError(
                            "无法为设备 {deviceId} 构建活动通话 Handler。",
                            deviceContext.DeviceId);
                        activeCallHandler.Dispose();
                        return false;
                    }

                    activeCallContext.RegisterCallOwnedResource(activeCallHandler);
                }

                foreach (IHandler handler in handlerContainer.Values)
                {
                    this.InitializeDeviceContext(deviceContext, handler);
                    if (!handler.Build())
                    {
                        this.Logger.LogError("无法为设备 {deviceId} 构建处理程序管道。", deviceContext.DeviceId);
                        foreach (IHandler createdHandler in handlerContainer.Values)
                        {
                            createdHandler.Dispose();
                        }
                        return false;
                    }
                }

                List<Action> completeWriters = [];
                List<Task> handlerTasks = [];
                this.BuildHandlersWorkflow(rtp, audioReceived, completeWriters, handlerTasks);
                this.BuildHandlersWorkflow(audioReceived, audio2Text, completeWriters, handlerTasks);
                this.BuildHandlersWorkflow(audio2Text, dialogue, completeWriters, handlerTasks);
                this.BuildHandlersWorkflow(dialogue, text2Audio, completeWriters, handlerTasks);
                this.BuildHandlersWorkflow(text2Audio, audioSend, completeWriters, handlerTasks);
                resources = new HandlerResourcesLifetime(
                    handlerContainer.Values.ToArray(),
                    new HandlerPipelineLifetime(completeWriters, handlerTasks, this.Logger));
                activeCallContext.AIAgentContext.RegisterOwnedResource(resources);
                resources = null;
            }
            catch (Exception exception)
            {
                if (resources is not null)
                {
                    resources.Dispose();
                }
                else
                {
                    activeCallHandler?.Dispose();
                    foreach (IHandler handler in handlerContainer.Values)
                    {
                        handler.Dispose();
                    }
                }
                this.Logger.LogError(
                    exception,
                    "为设备 {deviceId} 构建处理程序管道时失败。",
                    deviceContext.DeviceId);
                return false;
            }

            if (answerRequest is null)
            {
                return true;
            }

            bool answered = await activeCallHandler!.AnswerAsync(answerRequest);
            if (answered)
            {
                this.ServiceProvider
                    .GetRequiredService<InboundMessagePlayer>()
                    .Start(activeCallContext, text2Audio);
            }
            return answered;
        }

        private sealed class HandlerResourcesLifetime : IDisposable
        {
            private IReadOnlyList<IHandler>? _handlers;
            private HandlerPipelineLifetime? _pipeline;

            public HandlerResourcesLifetime(
                IReadOnlyList<IHandler> handlers,
                HandlerPipelineLifetime pipeline)
            {
                this._handlers = handlers;
                this._pipeline = pipeline;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref this._pipeline, null)?.Dispose();
                IReadOnlyList<IHandler>? handlers =
                    Interlocked.Exchange(ref this._handlers, null);
                if (handlers is null)
                {
                    return;
                }

                for (int index = handlers.Count - 1; index >= 0; index--)
                {
                    handlers[index].Dispose();
                }
            }
        }

        private void InitializeDeviceContext(DeviceContext deviceContext, IHandler handler)
        {
            handler.DeviceContext = deviceContext;
        }

        private void BuildHandlersWorkflow<T>(
            IOutAIAdapterHandler<T> previous,
            IInAIAdapterHandler<T> next,
            ICollection<Action> completeWriters,
            ICollection<Task> handlerTasks)
        {
            BoundedChannelOptions boundedChannelOptions = new BoundedChannelOptions(CHANNEL_CAPACITY)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            };
            Channel<Workflow<T>> channel = Channel.CreateBounded<Workflow<T>>(boundedChannelOptions);
            previous.NextWriter = channel.Writer;
            next.PreviousReader = channel.Reader;

            completeWriters.Add(() => channel.Writer.TryComplete());
            handlerTasks.Add(Task.Run(next.HandleAsync));
            this.Logger?.LogDebug("已构建处理程序工作流，上一步：{previous} -> 下一步：{next}", previous.GetType().Name, next.GetType().Name);
        }

        private sealed class HandlerPipelineLifetime : IDisposable
        {
            private IReadOnlyList<Action>? _completeWriters;
            private readonly IReadOnlyList<Task> _handlerTasks;
            private readonly ILogger _logger;

            public HandlerPipelineLifetime(
                IReadOnlyList<Action> completeWriters,
                IReadOnlyList<Task> handlerTasks,
                ILogger logger)
            {
                this._completeWriters = completeWriters;
                this._handlerTasks = handlerTasks;
                this._logger = logger;
            }

            public void Dispose()
            {
                IReadOnlyList<Action>? completeWriters =
                    Interlocked.Exchange(ref this._completeWriters, null);
                if (completeWriters is null)
                {
                    return;
                }

                foreach (Action complete in completeWriters)
                {
                    complete();
                }

                try
                {
                    Task.WhenAll(this._handlerTasks).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    this._logger.LogError(
                        exception,
                        "等待通话 Handler 后台管线结束时失败。");
                }
            }
        }
    }
}
