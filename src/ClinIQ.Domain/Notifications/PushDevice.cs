using ClinIQ.Domain.Common;

namespace ClinIQ.Domain.Notifications;

public enum PushPlatform { Android = 1, Ios = 2 }
public sealed class PushDevice : Entity
{
    public string InstallationSecretHash { get; private set; } = null!;
    public Guid? UserId { get; private set; }
    public Guid? SessionId { get; private set; }
    public string? TokenHash { get; private set; }
    public string? ProtectedToken { get; private set; }
    public PushPlatform Platform { get; private set; }
    public int BindingRevision { get; private set; }
    public DateTimeOffset ChangedAtUtc { get; private set; }
    public byte[] Version { get; private set; } = [];
    private PushDevice() { }
    private PushDevice(Guid id) : base(id) { }
    public static PushDevice Create(Guid installationId, string secretHash)
    {
        if (secretHash is not { Length: 64 } || !secretHash.All(Uri.IsHexDigit)) throw new ArgumentException("A hashed installation proof is required.");
        return new(installationId) { InstallationSecretHash = secretHash };
    }
    public void Bind(Guid user, Guid session, string tokenHash, string protectedToken, PushPlatform platform, DateTimeOffset now)
    {
        if (user == Guid.Empty || session == Guid.Empty || tokenHash.Length != 64 || string.IsNullOrEmpty(protectedToken) || !Enum.IsDefined(platform)) throw new ArgumentException("Invalid push binding.");
        if (UserId == user && SessionId == session && TokenHash == tokenHash && Platform == platform) return;
        UserId = user; SessionId = session; TokenHash = tokenHash; ProtectedToken = protectedToken; Platform = platform;
        BindingRevision = checked(BindingRevision + 1); ChangedAtUtc = now;
    }
    public void Detach(DateTimeOffset now)
    {
        if (UserId is null) return;
        UserId = null; SessionId = null; TokenHash = null; ProtectedToken = null;
        BindingRevision = checked(BindingRevision + 1); ChangedAtUtc = now;
    }
}
