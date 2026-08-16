using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Management;
using Agent.Telephone.Resources;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Agent.Telephone.Providers.CallControl
{
    /// <summary>
    /// Assistant role <see cref="ICallControl"/> implementation. It keeps the existing
    /// SIP dialogue and RTP session while replacing the Agent session
    /// (providers, function tools and handlers) with the target assistant.
    /// </summary>
    internal class AssistantRoleControl : BaseProvider<AssistantRoleControl, List<AssistantConfig>>, ICallControl
    {
        private readonly FunctionToolManager _functionToolManager;
        private readonly ProviderManager _providerManager;
        private readonly HandlerManager _handlerManager;
        private readonly IAudioPromptPlayer _audioPromptPlayer;

        private List<AssistantConfig> _assistantConfigs;

        public AssistantRoleControl(
            FunctionToolManager functionToolManager,
            ProviderManager providerManager,
            HandlerManager handlerManager,
            IAudioPromptPlayer audioPromptPlayer,
            ILogger<AssistantRoleControl> logger) : base(logger)
        {
            this._functionToolManager = functionToolManager;
            this._providerManager = providerManager;
            this._handlerManager = handlerManager;
            this._audioPromptPlayer = audioPromptPlayer;
            this._assistantConfigs = [];
        }

        public override string ProviderType => "call-control";

        public override string ModelName => nameof(AssistantRoleControl);

        public override bool Build(List<AssistantConfig> assistantConfig)
        {
            this._assistantConfigs = assistantConfig;
            return true;
        }

        public Task<AssistantSwitchResult> SwitchAssistantAsync(
            ActiveCallContext call,
            string targetAssistantNumber,
            CancellationToken cancellationToken = default)
        {
            AssistantSwitchResult validationSwitchResult =
                this.ValidateAssistantTarget(call, targetAssistantNumber);
            if (!validationSwitchResult.Succeeded)
            {
                return Task.FromResult(validationSwitchResult);
            }

            if (!call.UserAgent.IsCallActive)
            {
                this.Logger.LogWarning(
                    "当前通话已经结束，无法切换到目标 Agent，呼叫的号码为：{targetAssistantNumber}",
                    targetAssistantNumber);
                return Task.FromResult(new AssistantSwitchResult(
                    AssistantSwitchStatus.CallEnded,
                    targetAssistantNumber.Trim(),
                    "当前通话已经结束。"));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(new AssistantSwitchResult(
                    AssistantSwitchStatus.Failed,
                    targetAssistantNumber.Trim(),
                    "角色切换请求已取消。"));
            }

            if (!call.TryBeginAssistantSwitch())
            {
                this.Logger.LogWarning(
                    "当前通话正在切换 Agent，无法切换到目标 Agent，呼叫的号码为：{targetAssistantNumber}",
                    targetAssistantNumber);
                return Task.FromResult(new AssistantSwitchResult(
                    AssistantSwitchStatus.Failed,
                    targetAssistantNumber,
                    "当前通话正在切换 Agent。"));
            }

            if (!call.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                call.CompleteAssistantSwitch();
                return Task.FromResult(new AssistantSwitchResult(
                    AssistantSwitchStatus.CallEnded,
                    targetAssistantNumber,
                    "当前通话已经结束。"));
            }

            _ = this.SwitchAssistantInBackgroundAsync(call, targetAssistantNumber, lease);
            return Task.FromResult(new AssistantSwitchResult(
                AssistantSwitchStatus.Accepted,
                targetAssistantNumber));
        }

        private async Task SwitchAssistantInBackgroundAsync(
            ActiveCallContext activeCall,
            string targetAssistantNumber,
            IDisposable lease)
        {
            try
            {
                await this.SwitchAssistantCoreAsync(activeCall, targetAssistantNumber);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(
                    exception,
                    "在后台切换通话 {CallId} 到 Agent {TargetAssistantNumber} 时发生异常。",
                    activeCall.CallId,
                    targetAssistantNumber);
                activeCall.CompleteAssistantSwitch();
            }
            finally
            {
                lease.Dispose();
            }
        }

        /// <summary>
        /// 预先验证目标 Agent 是否有效，避免在切换过程中出现异常。
        /// </summary>
        private AssistantSwitchResult ValidateAssistantTarget(
            ActiveCallContext call,
            string targetAssistantNumber)
        {
            if (string.IsNullOrWhiteSpace(targetAssistantNumber))
            {
                this.Logger.LogWarning("目标 Agent 号码不能为空。");
                return new AssistantSwitchResult(
                    AssistantSwitchStatus.InvalidTarget,
                    targetAssistantNumber ?? string.Empty,
                    "目标 Agent 号码不能为空。");
            }

            string normalized = targetAssistantNumber.Trim();
            if (string.Equals(normalized, call.DialedNumber, StringComparison.OrdinalIgnoreCase))
            {
                this.Logger.LogWarning("不能切换到当前相同的 Agent。");
                return new AssistantSwitchResult(
                    AssistantSwitchStatus.CurrentAssistant,
                    normalized,
                    "不能切换到当前相同的 Agent。");
            }

            if (this._assistantConfigs.Any(assistant =>
                    string.Equals(
                        assistant.DialingNumber,
                        normalized,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return new AssistantSwitchResult(
                    AssistantSwitchStatus.Accepted,
                    normalized);
            }

            this.Logger.LogWarning(
                "目标 Agent 不存在，呼叫的号码为：{targetAssistantNumber}",
                targetAssistantNumber);
            return new AssistantSwitchResult(
                AssistantSwitchStatus.UnknownAssistant,
                normalized,
                "目标 Agent 不存在。");
        }

        private async Task<bool> SwitchAssistantCoreAsync(
            ActiveCallContext currentActiveCall,
            string targetAssistantNumber)
        {
            DeviceContext deviceContext = currentActiveCall.DeviceContext;
            if (!ReferenceEquals(deviceContext.ActiveCall, currentActiveCall))
            {
                return false;
            }

            using CancellationTokenSource ringbackCts =
                CancellationTokenSource.CreateLinkedTokenSource(currentActiveCall.CallToken);

            Task<bool> ringbackTask = this._audioPromptPlayer
                .PlaySIPCodeAudioLoopAsync(
                    SIPResponseStatusCodesEnum.Ringing,
                    currentActiveCall.VoIPRTP,
                    currentActiveCall.NegotiatedAudioFormat,
                    currentActiveCall.PacketTimeMs,
                    ringbackCts.Token);
            bool ringbackStopped = false;
            bool sessionReplaced = false;

            try
            {
                currentActiveCall.CallToken.ThrowIfCancellationRequested();

                if (!currentActiveCall.TryReplaceAssistantSession(targetAssistantNumber))
                {
                    this.Logger.LogWarning(
                        "无法在保持 SIP 通话的情况下切换到 Agent {TargetAssistantNumber}。",
                        targetAssistantNumber);
                    if (!currentActiveCall.CallToken.IsCancellationRequested &&
                        currentActiveCall.UserAgent.IsCallActive)
                    {
                        await this.StopRingbackAsync(ringbackCts, ringbackTask)
                            ;
                        ringbackStopped = true;
                    }

                    return false;
                }

                sessionReplaced = true;

                if (!await this.BuildAgentPipelineAsync(deviceContext))
                {
                    await this.StopRingbackAsync(ringbackCts, ringbackTask)
                        ;
                    ringbackStopped = true;
                    await this.PlayUnavailableAndEndAsync(currentActiveCall)
                        ;
                    return false;
                }

                await this.StopRingbackAsync(ringbackCts, ringbackTask)
                    ;
                ringbackStopped = true;
                currentActiveCall.ResumeAgentMedia();
                deviceContext.MarkCallConnected(currentActiveCall);
                this.Logger.LogInformation(
                    "设备 {DeviceId} 已在当前 SIP 通话中切换到 Agent {TargetAssistantNumber}。",
                    currentActiveCall.DeviceId,
                    targetAssistantNumber);

                return true;
            }
            catch (OperationCanceledException) when (
                currentActiveCall.CallToken.IsCancellationRequested)
            {
                this.Logger.LogDebug(
                    "设备 {DeviceId} 的 Agent 切换因通话结束而取消。",
                    currentActiveCall.DeviceId);
                return false;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(
                    exception,
                    "在当前 SIP 通话中切换到 Agent {TargetAssistantNumber} 时发生异常。",
                    targetAssistantNumber);

                if (!currentActiveCall.CallToken.IsCancellationRequested &&
                    currentActiveCall.UserAgent.IsCallActive)
                {
                    await this.StopRingbackAsync(ringbackCts, ringbackTask)
                        ;
                    ringbackStopped = true;
                    if (sessionReplaced)
                    {
                        await this.PlayUnavailableAndEndAsync(currentActiveCall)
                            ;
                    }
                }

                return false;
            }
            finally
            {
                if (!ringbackStopped)
                {
                    await this.StopRingbackAsync(ringbackCts, ringbackTask)
                        ;
                }

                // 原 Agent 会话仍然存活时，恢复其媒体处理，让当前通话继续。
                if (!sessionReplaced && currentActiveCall.UserAgent.IsCallActive)
                {
                    currentActiveCall.ResumeAgentMedia();
                }

                currentActiveCall.CompleteAssistantSwitch();
            }
        }

        private async Task<bool> BuildAgentPipelineAsync(DeviceContext deviceContext)
        {
            return await this._functionToolManager
                .BuildForActiveCallAsync(deviceContext)
                 &&
                await this._providerManager.BuildForActiveCallAsync(deviceContext)
                     &&
                await this._handlerManager.BuildForConnectedCallAsync(deviceContext)
                    ;
        }

        private async Task StopRingbackAsync(
            CancellationTokenSource ringbackCts,
            Task<bool> ringbackTask)
        {
            try
            {
                await ringbackCts.CancelAsync();
                await ringbackTask;
            }
            catch (Exception exception)
            {
                this.Logger.LogWarning(exception, "停止 Agent 切换回铃音时发生异常。");
            }
        }

        private async Task PlayUnavailableAndEndAsync(ActiveCallContext activeCall)
        {
            try
            {
                activeCall.MarkPlayingPrompt();
                await this._audioPromptPlayer.PlaySIPCodeAudioAsync(
                    SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                    activeCall.VoIPRTP,
                    activeCall.NegotiatedAudioFormat,
                    activeCall.PacketTimeMs,
                    activeCall.CallToken);
            }
            catch (Exception exception)
            {
                this.Logger.LogWarning(exception, "播放 Agent 不可用提示音时发生异常。");
            }
            finally
            {
                if (activeCall.UserAgent.IsCallActive)
                {
                    activeCall.MarkEnding();
                    activeCall.UserAgent.Hangup();
                }
            }
        }

        public override void Dispose()
        {
        }
    }
}
