using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.LLM.AIContextProviders;
using Agent.Telephone.Providers.LLM.Agents;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Providers.LLM
{
    internal class GenericOpenAI : BaseProvider<GenericOpenAI, LLMBuildConfig>, ILlm
    {
        private readonly IServiceProvider _serviceProvider;

        private readonly ObjectPool<OutSegment> _outSegmentPool;
        private readonly Dictionary<string, IAgent> _subAgents = new Dictionary<string, IAgent>();
        private readonly SessionChatHistoryProvider _chatHistoryProvider;
        private Workflow? _dialogueWorkflow;
        private ILlmEventCallback? _eventCallback;
        private int _seqParagraphId = 0;
        private int _seqSentenceId = 0;
        private int _responseTimeoutSeconds = 30;

        public GenericOpenAI(IServiceProvider serviceProvider,
            SessionChatHistoryProvider chatHistoryProvider,
            ObjectPool<OutSegment> outSegmentPool,
            ILogger<GenericOpenAI> logger) : base(logger)
        {
            this._serviceProvider = serviceProvider;
            this._chatHistoryProvider = chatHistoryProvider;

            this._outSegmentPool = outSegmentPool;
            this._subAgents = new Dictionary<string, IAgent>();
        }
        public override string ModelName => nameof(GenericOpenAI);
        public override string ProviderType => "llm";

        public override bool Build(LLMBuildConfig modelSetting)
        {
            try
            {
                this._subAgents.Clear();
                this._dialogueWorkflow = null;
                this._responseTimeoutSeconds = modelSetting.ResponseTimeoutSeconds
                    ?? modelSetting.AgentSettings[SubAgentNames.ChatAgent].Config.GetConfigValueOrDefault("ResponseTimeoutSeconds", 30);
                // CancelAfter 的有限时长上限为 uint.MaxValue - 1 毫秒。
                if (this._responseTimeoutSeconds < 0 || this._responseTimeoutSeconds > 4_294_967)
                {
                    throw new ArgumentOutOfRangeException(nameof(modelSetting), "LLM 响应超时必须为 0（不限时长）或 1 到 4294967 秒。");
                }

                IAgent inputAgent = this._serviceProvider.GetRequiredKeyedService<IAgent>(SubAgentNames.InputAgent);
                IAgent intentDetectionAgent = this._serviceProvider.GetRequiredKeyedService<IAgent>(SubAgentNames.IntentDetectionAgent);
                IAgent functionCallAgent = this._serviceProvider.GetRequiredKeyedService<IAgent>(SubAgentNames.FunctionCallAgent);
                IAgent intentResponseAgent = this._serviceProvider.GetRequiredKeyedService<IAgent>(SubAgentNames.IntentResponseAgent);
                IAgent chatAgent = this._serviceProvider.GetRequiredKeyedService<IAgent>(SubAgentNames.ChatAgent);
                IAgent outputAgent = this._serviceProvider.GetRequiredKeyedService<IAgent>(SubAgentNames.OutputAgent);

                this._subAgents[inputAgent.AgentName] = inputAgent;
                this._subAgents[intentDetectionAgent.AgentName] = intentDetectionAgent;
                this._subAgents[functionCallAgent.AgentName] = functionCallAgent;
                this._subAgents[intentResponseAgent.AgentName] = intentResponseAgent;
                this._subAgents[chatAgent.AgentName] = chatAgent;
                this._subAgents[outputAgent.AgentName] = outputAgent;

                bool buildSuccess = this._subAgents.Values
                    .AsParallel()
                    .Select(agent =>
                    {
                        if (modelSetting.AgentSettings.TryGetValue(agent.AgentName, out ModelSetting? agentSetting))
                        {
                            return agent.Build(new LLMAgentBuildConfig(agentSetting, modelSetting.SessionPrivateProvider, this._chatHistoryProvider));
                        }
                        else
                        {
                            this.Logger.LogError("构建 Sub Agent {agentName} 失败。", agent.AgentName);
                            return false;
                        }
                    })
                    .All(r => r);

                if (buildSuccess)
                {
                    this._dialogueWorkflow = this.BuildDialogueWorkflow(modelSetting.SessionPrivateProvider);
                }
                return buildSuccess;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "Failed to build {providerType} provider {modelName}.", this.ProviderType, this.ModelName);
                return false;
            }
        }

        private Workflow BuildDialogueWorkflow(PrivateProvider privateProvider)
        {
            ExecutorBinding inputExecutor = this._subAgents[SubAgentNames.InputAgent].AsExecutor();
            ExecutorBinding chatExecutor = this._subAgents[SubAgentNames.ChatAgent].AsExecutor();
            ExecutorBinding outputExecutor = this._subAgents[SubAgentNames.OutputAgent].AsExecutor();
            ExecutorBinding intentSubWorkflow = this.BuildIntentSubWorkflow(privateProvider)
                .BindAsExecutor($"{NormalizeDeviceId(privateProvider.DeviceId)}_intent_binding");

            return new WorkflowBuilder(inputExecutor)
                .AddEdge<WorkflowPreInputs>(inputExecutor, intentSubWorkflow, iw => iw is not null && iw.IntentRequired)
                .AddEdge<WorkflowPreInputs>(inputExecutor, chatExecutor, iw => iw is not null && !iw.IntentRequired)
                .AddEdge<IntentDetectionResult>(intentSubWorkflow, chatExecutor, id => id is not null && !id.Detected)
                .AddEdge(intentSubWorkflow, outputExecutor)
                .AddEdge(chatExecutor, outputExecutor)
                .WithOutputFrom(outputExecutor)
                .WithName(privateProvider.DeviceId)
                .WithDescription($"Dialogue workflow for device {privateProvider.DeviceId}")
                .Build();
        }

        private Workflow BuildIntentSubWorkflow(PrivateProvider privateProvider)
        {
            ExecutorBinding intentDetectionExecutor = this._subAgents[SubAgentNames.IntentDetectionAgent].AsExecutor();
            ExecutorBinding functionCallExecutor = this._subAgents[SubAgentNames.FunctionCallAgent].AsExecutor();
            ExecutorBinding intentResponseExecutor = this._subAgents[SubAgentNames.IntentResponseAgent].AsExecutor();

            return new WorkflowBuilder(intentDetectionExecutor)
                .AddEdge<IntentDetectionResult>(intentDetectionExecutor, functionCallExecutor, dr => dr is not null && dr.Detected)
                .AddEdge<IntentDetectionResult>(intentDetectionExecutor, intentResponseExecutor, dr => dr is not null && !dr.Detected)
                .AddEdge(functionCallExecutor, intentResponseExecutor)
                .WithOutputFrom(intentResponseExecutor)
                .WithName($"{privateProvider.DeviceId}_intent")
                .WithDescription($"Intent workflow for device {privateProvider.DeviceId}")
                .Build();
        }

        public void RegisterDevice(ActiveCallContext activeCall, ILlmEventCallback callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            this._eventCallback = callback;
            this._chatHistoryProvider.Bind(activeCall.AIAgentContext.ChatHistory);

            foreach (var agent in this._subAgents.Values)
            {
                agent.RegisterDevice(activeCall.DeviceId);
            }
            base.RegisterDevice(activeCall);
        }

        public override void UnregisterDevice(ActiveCallContext activeCall)
        {
            foreach (var agent in this._subAgents.Values)
            {
                agent.UnregisterDevice(activeCall.DeviceId);
            }
            if (ReferenceEquals(this.CurrentCall, activeCall))
            {
                this._eventCallback = null;
            }
            base.UnregisterDevice(activeCall);
        }

        public async Task StartDialogueAsync(long turnId, string userMessage, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(this.CurrentCall.DeviceId))
            {
                throw new SessionNotInitializedException();
            }
            if (!this._subAgents.Any())
            {
                this.Logger.LogError("{providerType} provider {modelName} has not been built.", this.ProviderType, this.ModelName);
                return;
            }
            if (this._dialogueWorkflow is null)
            {
                throw new InvalidOperationException("Dialogue workflow is not initialized.");
            }

            ILlmEventCallback eventCallback = this._eventCallback
                ?? throw new InvalidOperationException("The LLM event callback has not been registered.");

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (this._responseTimeoutSeconds > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(this._responseTimeoutSeconds));
            }
            try
            {
                await this.RunAndEmitWorkflowStreamingAsync(
                    this._dialogueWorkflow,
                    turnId,
                    userMessage,
                    eventCallback,
                    timeoutCts.Token);
                await eventCallback.OnCompletedAsync(turnId, CancellationToken.None);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                await eventCallback.OnFailedAsync(
                    turnId,
                    new TimeoutException($"LLM response exceeded {this._responseTimeoutSeconds} seconds."),
                    CancellationToken.None);
                this.Logger.LogWarning(
                    "LLM 对话在 {timeoutSeconds} 秒内未完成，设备 {deviceId}。",
                    this._responseTimeoutSeconds,
                    this.CurrentCall.DeviceId);
                throw new TimeoutException($"LLM 对话超过 {this._responseTimeoutSeconds} 秒未完成。");
            }
            catch (OperationCanceledException)
            {
                await eventCallback.OnCancelledAsync(turnId, CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                await eventCallback.OnFailedAsync(turnId, exception, CancellationToken.None);
                throw;
            }
        }

        protected override string GenerateId()
        {
            int sequence = Interlocked.Increment(ref this._seqParagraphId);
            return $"{this.CurrentCall.DeviceId}_{sequence}";
        }

        private string GenerateSentenceId(string paragraphId)
        {
            return $"{paragraphId}_{Interlocked.Increment(ref this._seqSentenceId)}";
        }

        private async Task RunAndEmitWorkflowStreamingAsync(
            Workflow dialogueWorkflow,
            long turnId,
            string userMessage,
            ILlmEventCallback eventCallback,
            CancellationToken token)
        {
            List<OutSegment> allSegments = new List<OutSegment>();
            string paragraphId = this.GenerateId();
            int deliveredSegmentCount = 0;
            OutSegment? pendingSegment = null; // 缓冲上一句，等待确认是否为最后一段
            StreamingRun? run = null;
            using SemaphoreSlim promptGate = new(1, 1);

            Guid promptRegistrationId = ToolExecutionPromptDispatcher.Register(this.CurrentCall.DeviceId, this.CurrentCall.AIAgentContext.PrivateProvider, async (prompt, callbackToken) =>
            {
                await promptGate.WaitAsync(callbackToken);
                OutSegment segment = this._outSegmentPool.Get();
                try
                {
                    segment.Initialize(prompt, true, true, paragraphId, this.GenerateSentenceId(paragraphId));
                    await eventCallback.OnToolExecutionPromptAsync(turnId, segment, callbackToken);
                }
                finally
                {
                    this._outSegmentPool.Return(segment);
                    promptGate.Release();
                }
            });

            try
            {
                run = await InProcessExecution.Concurrent.RunStreamingAsync(dialogueWorkflow, userMessage, this.CurrentCall.DeviceId, token);
                await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(token))
                {
                    token.ThrowIfCancellationRequested();

                    switch (workflowEvent)
                    {
                        case WorkflowOutputEvent workflowOutputEvent when workflowOutputEvent.Data is WorkflowOutputs output:

                            this.Logger.LogDebug("Content {content}.", output.ResponseText);
                            if (pendingSegment is not null)
                            {
                                allSegments.Add(pendingSegment);
                                if (deliveredSegmentCount == 0)
                                {
                                    await eventCallback.OnBeforeFirstSegmentAsync(turnId, pendingSegment, token);
                                }
                                await eventCallback.OnSegmentAsync(turnId, pendingSegment, token);
                                deliveredSegmentCount++;
                            }

                            OutSegment segment = this._outSegmentPool.Get();
                            segment.Initialize(output.ResponseText, deliveredSegmentCount == 0, false, paragraphId, this.GenerateSentenceId(paragraphId));
                            pendingSegment = segment;
                            break;

                        case WorkflowErrorEvent workflowErrorEvent:
                            throw workflowErrorEvent.Exception ?? new InvalidOperationException("Dialogue workflow failed.");
                        case ExecutorFailedEvent executorFailedEvent:
                            throw new InvalidOperationException($"Executor '{executorFailedEvent.ExecutorId}' failed with {(executorFailedEvent.Data is null ? "unknown error" : executorFailedEvent.Data)}.");
                    }
                }

                token.ThrowIfCancellationRequested();

                // 流结束，将最后一句标记为IsLastSegment后发出
                if (pendingSegment is not null)
                {
                    pendingSegment.IsLastSegment = true;
                    allSegments.Add(pendingSegment);
                    if (deliveredSegmentCount == 0)
                    {
                        await eventCallback.OnBeforeFirstSegmentAsync(turnId, pendingSegment, token);
                    }
                    await eventCallback.OnSegmentAsync(turnId, pendingSegment, token);
                }

            }
            catch (OperationCanceledException)
            {
                // 确保pending segment归入allSegments以便池回收
                if (pendingSegment is not null)
                {
                    allSegments.Add(pendingSegment);
                }
                // 停止观察事件流不会取消执行器，必须显式取消运行中的模型和工具。
                if (run is not null)
                {
                    await run.CancelRunAsync();
                }
                this.Logger.LogDebug("Dialogue workflow cancelled after {count} segments.", allSegments.Count);
                throw;
            }
            catch (Exception ex)
            {
                if (pendingSegment is not null)
                {
                    allSegments.Add(pendingSegment);
                }
                this.Logger.LogError(ex, "Unexpected error in {providerType} dialogue workflow.", this.ProviderType);
                throw;
            }
            finally
            {
                ToolExecutionPromptDispatcher.Unregister(this.CurrentCall.DeviceId, promptRegistrationId);
                if (run is not null)
                {
                    await run.DisposeAsync();
                }
                foreach (OutSegment segment in allSegments.Distinct())
                {
                    this._outSegmentPool.Return(segment);
                }
            }
        }

        public override void Dispose()
        {
            foreach (var agent in this._subAgents.Values)
            {
                agent.Dispose();
            }
            this._subAgents.Clear();
            this._dialogueWorkflow = null;
        }

        private static string NormalizeDeviceId(string deviceId)
        {
            return string.Concat(deviceId.Where(char.IsLetterOrDigit));
        }
    }
}
