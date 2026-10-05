namespace Agent.Telephone.Providers.CallControl.Reservations
{
    internal enum RegisteredEndpointStatus
    {
        Available = 0,
        Offline,
        Busy,
    }

    internal sealed record RegisteredEndpoint(
        string DialingNumber,
        string ContactUri);

    internal interface IRegisteredEndpointLease : IDisposable
    {
        RegisteredEndpoint Endpoint { get; }
    }

    internal sealed class RegisteredEndpointLease : IRegisteredEndpointLease
    {
        private Action? _release;

        public RegisteredEndpointLease(RegisteredEndpoint endpoint, Action release)
        {
            this.Endpoint = endpoint;
            this._release = release;
        }

        public RegisteredEndpoint Endpoint { get; }

        public void Dispose() => Interlocked.Exchange(ref this._release, null)?.Invoke();
    }

    internal sealed record RegisteredEndpointResolution(
        RegisteredEndpointStatus Status,
        IRegisteredEndpointLease? Lease = null);

    /// <summary>
    /// Registrar boundary implemented by the device manager. An available endpoint
    /// has a current, non-expired registration and is reserved for the caller.
    /// </summary>
    internal interface IRegisteredEndpointDirectory
    {
        ValueTask<RegisteredEndpointResolution> AcquireAsync(
            string dialingNumber,
            CancellationToken cancellationToken);
    }
}
