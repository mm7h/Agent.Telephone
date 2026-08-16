using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Store;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Providers.CallControl.Reservations;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;

namespace Agent.Telephone.Management
{
    internal class DeviceContextManager : BaseManager, IRegisteredEndpointDirectory
    {
        private readonly IStore _connectionStore;
        private readonly ITelephoneStore? _telephoneStore;
        private readonly object _deviceLock = new();
        private readonly TransferReservationRegistry _reservations;

        public DeviceContextManager(
            IStore store,
            IServiceProvider serviceProvider,
            TransferReservationRegistry reservations,
            TelephoneConfig config,
            ILogger<DeviceContextManager> logger)
            : base(serviceProvider, config, logger)
        {
            this._connectionStore = store;
            this._telephoneStore = serviceProvider.GetService<ITelephoneStore>();
            this._reservations = reservations;
        }

        public static IHostBuilder RegisterServices(IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<DeviceContextManager>();
                services.AddSingleton<IRegisteredEndpointDirectory>(
                    serviceProvider => serviceProvider.GetRequiredService<DeviceContextManager>());
            });
        }

        public override bool BuildComponent() => true;

        public override async Task OnSIPDeviceRegisteringAsync(SIPTransport sipTransport, SIPRequest sipRequest)
        {
            (SIPURI contact, int expiresSeconds) = sipRequest.GetRegistration();
            if (expiresSeconds == 0)
            {
                if (this._telephoneStore is not null)
                {
                    await this._telephoneStore.RemoveDeviceRegistrationAsync(sipRequest.GetDeviceId());
                }

                this.UnregisterSIPDevice(sipRequest);
            }
            else
            {
                DateTimeOffset now = DateTimeOffset.Now;
                if (this._telephoneStore is not null)
                {
                    DeviceContext? existing = this.GetSIPDeviceById(sipRequest);
                    DateTimeOffset registeredAt = existing?.Registration?.RegisteredAt ?? now;
                    await this._telephoneStore.SaveDeviceRegistrationAsync(new DeviceRegistrationRecord(
                        sipRequest.GetDeviceId(),
                        sipRequest.GetCallerAor().ToString(),
                        contact.ToString(),
                        registeredAt,
                        now,
                        now.AddSeconds(expiresSeconds)));
                }

                this.GetOrRegisterSIPDevice(sipTransport, sipRequest, contact, expiresSeconds);
            }
        }

        public async Task RestoreRegistrationsAsync(CancellationToken cancellationToken)
        {
            if (this._telephoneStore is null)
            {
                return;
            }

            IReadOnlyList<DeviceRegistrationRecord> registrations = await this._telephoneStore
                .GetActiveDeviceRegistrationsAsync(DateTimeOffset.Now, cancellationToken);
            foreach (DeviceRegistrationRecord registration in registrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lock (this._deviceLock)
                    {
                        if (!this._connectionStore.Contains(registration.DeviceId))
                        {
                            this._connectionStore.Add(
                                registration.DeviceId,
                                new DeviceContext(
                                    this.ServiceProvider.GetRequiredService<SIPTransport>(),
                                    registration,
                                    this.Config.AssistantConfigs,
                                    this.Logger));
                        }
                    }
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
                {
                    this.Logger.LogWarning(exception, "Unable to restore SIP registration for device {DeviceId}.", registration.DeviceId);
                    await this._telephoneStore.RemoveDeviceRegistrationAsync(registration.DeviceId, cancellationToken);
                }
            }
        }

        public DeviceContext GetOrRegisterSIPDevice(
            SIPTransport sipTransport,
            SIPRequest sipRequest,
            SIPURI contact,
            int expiresSeconds)
        {
            lock (this._deviceLock)
            {
                DeviceContext? existing = this.GetSIPDeviceById(sipRequest);
                if (existing is not null)
                {
                    existing.UpdateRegistration(sipRequest, contact, expiresSeconds);
                    return existing;
                }

                return this.RegisterSIPDevice(sipTransport, sipRequest, contact, expiresSeconds);
            }
        }

        public DeviceContext RegisterSIPDevice(
            SIPTransport sipTransport,
            SIPRequest sipRequest,
            SIPURI contact,
            int expiresSeconds)
        {
            DeviceContext deviceContext = new DeviceContext(
                sipTransport,
                sipRequest,
                contact,
                expiresSeconds,
                this.Config.AssistantConfigs,
                this.Logger);
            this._connectionStore.Add(deviceContext.DeviceId, deviceContext);
            return deviceContext;
        }

        public DeviceContext? GetSIPDeviceById(SIPRequest sipRequest)
        {
            string deviceId = sipRequest.GetDeviceId();
            return this._connectionStore.Contains(deviceId)
                ? this._connectionStore.Get<DeviceContext>(deviceId)
                : null;
        }

        public DeviceContext? GetRegisteredSIPDeviceById(SIPRequest sipRequest)
        {
            DeviceContext? device = this.GetSIPDeviceById(sipRequest);
            return device?.IsRegistered() == true ? device : null;
        }

        public void UnregisterSIPDevice(SIPRequest sipRequest)
        {
            this.GetSIPDeviceById(sipRequest)?.Unregister();
        }

        public override Task OnSIPDeviceUnregisterAsync(
            DeviceContext deviceContext,
            SIPTransport sipTransport,
            SIPRequest sipRequest)
        {
            deviceContext.Unregister();
            return Task.CompletedTask;
        }

        public ValueTask<RegisteredEndpointResolution> AcquireAsync(
            string dialingNumber,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dialingNumber))
            {
                return ValueTask.FromResult(new RegisteredEndpointResolution(RegisteredEndpointStatus.Offline));
            }

            lock (this._deviceLock)
            {
                DeviceContext? device = this._connectionStore
                    .Get<DeviceContext>(candidate =>
                        candidate.TryGetActiveRegistration(out RegistrationBinding? binding) &&
                        string.Equals(binding!.Aor.User, dialingNumber, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (device is null ||
                    !device.TryGetActiveRegistration(out RegistrationBinding? activeBinding) ||
                    activeBinding is null)
                {
                    return ValueTask.FromResult(new RegisteredEndpointResolution(RegisteredEndpointStatus.Offline));
                }

                RegisteredEndpoint endpoint = new(
                    dialingNumber,
                    activeBinding.Contact.ToString());
                if (device.IsCallOccupied ||
                    !this._reservations.TryAcquire(
                        device.DeviceId,
                        endpoint,
                        out IRegisteredEndpointLease? lease))
                {
                    return ValueTask.FromResult(new RegisteredEndpointResolution(RegisteredEndpointStatus.Busy));
                }

                return ValueTask.FromResult(new RegisteredEndpointResolution(
                    RegisteredEndpointStatus.Available,
                    lease));
            }
        }

        public ValueTask<RegisteredEndpointResolution> AcquireCallbackAsync(
            string userAor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SIPURI parsedAor;
            try
            {
                parsedAor = SIPURI.ParseSIPURI(userAor);
            }
            catch
            {
                return ValueTask.FromResult(
                    new RegisteredEndpointResolution(RegisteredEndpointStatus.Offline));
            }

            lock (this._deviceLock)
            {
                DeviceContext? device = this._connectionStore
                    .Get<DeviceContext>(candidate =>
                        candidate.TryGetActiveRegistration(out RegistrationBinding? candidateBinding) &&
                        string.Equals(
                            candidateBinding!.Aor.User,
                            parsedAor.User,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            candidateBinding.Aor.Host,
                            parsedAor.Host,
                            StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (device is null ||
                    !device.TryGetActiveRegistration(out RegistrationBinding? activeBinding) ||
                    activeBinding is null)
                {
                    return ValueTask.FromResult(
                        new RegisteredEndpointResolution(RegisteredEndpointStatus.Offline));
                }

                if (!device.TryBeginCallback())
                {
                    return ValueTask.FromResult(
                        new RegisteredEndpointResolution(RegisteredEndpointStatus.Busy));
                }

                RegisteredEndpoint endpoint = new(
                    activeBinding.Aor.User,
                    activeBinding.Contact.ToString());
                if (!this._reservations.TryAcquire(
                    device.DeviceId,
                    endpoint,
                    out IRegisteredEndpointLease? lease,
                    device.EndCallback))
                {
                    device.EndCallback();
                    return ValueTask.FromResult(
                        new RegisteredEndpointResolution(RegisteredEndpointStatus.Busy));
                }

                return ValueTask.FromResult(new RegisteredEndpointResolution(
                    RegisteredEndpointStatus.Available,
                    lease));
            }
        }

        public bool TryAttachCallbackCallSession(
            string userAor,
            string assistantNumber,
            SIPUserAgent userAgent,
            VoIPMediaSession mediaSession,
            out DeviceContext? device,
            out ActiveCallContext? activeCall)
        {
            device = null;
            activeCall = null;
            SIPURI parsedAor;
            try
            {
                parsedAor = SIPURI.ParseSIPURI(userAor);
            }
            catch
            {
                return false;
            }

            lock (this._deviceLock)
            {
                DeviceContext? candidate = this._connectionStore
                    .Get<DeviceContext>(item =>
                        item.TryGetActiveRegistration(out RegistrationBinding? binding) &&
                        string.Equals(
                            binding!.Aor.User,
                            parsedAor.User,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            binding.Aor.Host,
                            parsedAor.Host,
                            StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (candidate is null ||
                    !candidate.TryGetActiveRegistration(out RegistrationBinding? activeBinding) ||
                    activeBinding is null ||
                    !candidate.TryAttachCallbackCallSession(
                        userAor,
                        assistantNumber,
                        userAgent,
                        mediaSession,
                        out ActiveCallContext? attachedCall) ||
                    attachedCall is null)
                {
                    return false;
                }

                device = candidate;
                activeCall = attachedCall;
                return true;
            }
        }

    }
}
