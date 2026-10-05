namespace Agent.Telephone.Abstractions.Persistence
{
    /// <summary>
    /// Represents the durable portion of a SIP device registration.
    /// </summary>
    public sealed record DeviceRegistrationRecord(
        string DeviceId,
        string Aor,
        string Contact,
        DateTimeOffset RegisteredAt,
        DateTimeOffset RefreshedAt,
        DateTimeOffset ExpiresAt);
}
