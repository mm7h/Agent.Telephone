using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers.CallControl
{
    /// <summary>
    /// Owns call-control state that is shared by registrar lookups and active calls.
    /// The registrar remains responsible for resolving current bindings; this provider
    /// serializes temporary reservations so a target cannot be dialled concurrently.
    /// </summary>
    internal sealed class CallControlProvider
        : BaseProvider<CallControlProvider, ModelSetting>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly TelephoneConfig _config;
        private readonly TransferReservationRegistry _reservations = new();

        public CallControlProvider(
            IServiceProvider serviceProvider,
            TelephoneConfig config,
            ILogger<CallControlProvider> logger)
            : base(logger)
        {
            this._serviceProvider = serviceProvider;
            this._config = config;
        }

        public override string ProviderType => "call-control";

        public override string ModelName => nameof(CallControlProvider);

        public override bool Build(ModelSetting settings) => true;

        public bool TryAcquireTarget(
            string deviceId,
            RegisteredEndpoint endpoint,
            out IRegisteredEndpointLease? lease,
            Action? onReleased = null)
        {
            return this._reservations.TryAcquire(deviceId, endpoint, out lease, onReleased);
        }

        public async Task<CallTransferResult> TransferAsync(
            ActiveCallContext call,
            string targetNumber,
            CancellationToken cancellationToken = default)
        {
            CallTransferResult? validationFailure =
                CallTransferPolicy.ValidateTarget(targetNumber, call.CallerNumber);
            if (validationFailure is not null)
            {
                await this.PlayFailureIfPossibleAsync(
                    call,
                    validationFailure,
                    cancellationToken).ConfigureAwait(false);
                return validationFailure;
            }

            if (!call.UserAgent.IsCallActive)
            {
                return new CallTransferResult(
                    CallTransferStatus.CallEnded,
                    targetNumber.Trim(),
                    "原通话已经结束。");
            }

            AudioCodecsEnum codec = call.NegotiatedAudioFormat.Codec;
            if (codec is not AudioCodecsEnum.PCMU and not AudioCodecsEnum.PCMA)
            {
                CallTransferResult codecFailure = new(
                    CallTransferStatus.CodecNotSupported,
                    targetNumber.Trim(),
                    "原通话没有协商 PCMU 或 PCMA。");
                await this.PlayFailureIfPossibleAsync(
                    call,
                    codecFailure,
                    cancellationToken).ConfigureAwait(false);
                return codecFailure;
            }

            IRegisteredEndpointDirectory? directory = this._serviceProvider
                .GetService<IRegisteredEndpointDirectory>();
            RegisteredEndpointResolution resolution = directory is null
                ? new RegisteredEndpointResolution(RegisteredEndpointStatus.Offline)
                : await directory.AcquireAsync(targetNumber.Trim(), cancellationToken)
                    .ConfigureAwait(false);
            CallTransferResult? endpointFailure =
                CallTransferPolicy.FromEndpointStatus(targetNumber.Trim(), resolution);
            if (endpointFailure is not null)
            {
                await this.PlayFailureIfPossibleAsync(
                    call,
                    endpointFailure,
                    cancellationToken).ConfigureAwait(false);
                return endpointFailure;
            }

            IRegisteredEndpointLease lease = resolution.Lease
                ?? throw new InvalidOperationException(
                    "Available transfer endpoint did not provide a reservation lease.");
            ICallTransferExecutor? executor = call.AIAgentContext
                .PrivateProvider.CallTransferExecutor;
            if (executor is null)
            {
                lease.Dispose();
                return new CallTransferResult(
                    CallTransferStatus.CallEnded,
                    targetNumber.Trim(),
                    "通话控制 Handler 尚未就绪。");
            }

            return await executor.TransferAsync(
                new CallTransferCommand(
                    call,
                    lease.Endpoint,
                    lease,
                    codec,
                    Math.Max(1, this._config.SIPConfig.TransferTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task PlayFailureIfPossibleAsync(
            ActiveCallContext call,
            CallTransferResult result,
            CancellationToken cancellationToken)
        {
            ICallTransferExecutor? executor = call.AIAgentContext
                .PrivateProvider.CallTransferExecutor;
            if (executor is not null && call.UserAgent.IsCallActive)
            {
                await executor.PlayFailureAsync(call, result, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public override void Dispose() => this._reservations.Dispose();
    }
}
