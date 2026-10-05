using System.Collections.Concurrent;

namespace Agent.Telephone.Providers.CallControl.Reservations
{
    /// <summary>
    /// Ensures that a registered endpoint can be reserved by only one outbound
    /// transfer or callback attempt at a time.
    /// </summary>
    internal sealed class TransferReservationRegistry : IDisposable
    {
        private readonly ConcurrentDictionary<string, Reservation> _reservations =
            new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        public bool TryAcquire(
            string deviceId,
            RegisteredEndpoint endpoint,
            out IRegisteredEndpointLease? lease,
            Action? onReleased = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ArgumentNullException.ThrowIfNull(endpoint);

            lease = null;
            if (Volatile.Read(ref this._disposed) != 0)
            {
                return false;
            }

            Reservation reservation = new(onReleased);
            if (!this._reservations.TryAdd(deviceId, reservation))
            {
                return false;
            }

            if (Volatile.Read(ref this._disposed) != 0)
            {
                this.Release(deviceId, reservation);
                return false;
            }

            lease = new RegisteredEndpointLease(
                endpoint,
                () => this.Release(deviceId, reservation));
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) != 0)
            {
                return;
            }

            foreach ((string deviceId, Reservation reservation) in this._reservations)
            {
                this.Release(deviceId, reservation);
            }
        }

        private void Release(string deviceId, Reservation reservation)
        {
            if (!this._reservations.TryGetValue(deviceId, out Reservation? current) ||
                !ReferenceEquals(current, reservation) ||
                !((ICollection<KeyValuePair<string, Reservation>>)this._reservations).Remove(
                    new KeyValuePair<string, Reservation>(deviceId, reservation)))
            {
                return;
            }

            reservation.Release();
        }

        private sealed class Reservation
        {
            private Action? _onReleased;

            public Reservation(Action? onReleased)
            {
                this._onReleased = onReleased;
            }

            public void Release() => Interlocked.Exchange(ref this._onReleased, null)?.Invoke();
        }
    }
}
