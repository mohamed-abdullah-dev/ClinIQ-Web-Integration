namespace ClinIQ.Infrastructure.Accounts;

// Infrastructure credential, never exposed in an API response. Only its random secret's digest is persisted.
public sealed class DoctorResubmissionGrant
{
    public Guid Id { get; private set; }
    public Guid RegistrationId { get; private set; }
    public Guid UserId { get; private set; }
    public string SecretHash { get; private set; } = null!;
    public string SecurityStamp { get; private set; } = null!;
    public string NormalizedEmail { get; private set; } = null!;
    public byte[] RegistrationVersion { get; private set; } = [];
    public DateTimeOffset IssuedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }
    private DoctorResubmissionGrant() { }
    internal DoctorResubmissionGrant(Guid registrationId, Guid userId, string hash, string stamp,
        string email, byte[] version, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); RegistrationId = registrationId; UserId = userId; SecretHash = hash;
        SecurityStamp = stamp; NormalizedEmail = email; RegistrationVersion = version.ToArray();
        IssuedAtUtc = now; ExpiresAtUtc = now.AddHours(1);
    }
    internal void Consume(DateTimeOffset now) => ConsumedAtUtc = now;
}
