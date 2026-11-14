using ClinIQ.Domain.Accounts;
using Xunit;

namespace ClinIQ.Domain.Tests;

public sealed class AuthSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static AuthSession Create(bool remember = false) => AuthSession.Create(Guid.NewGuid(), SessionClient.Web, "stamp", new string('A', 64), Now, remember);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 7)]
    public void SessionHasBoundedLifetime(bool remember, int days)
    {
        var session = Create(remember);
        Assert.Equal(Now.AddDays(days), session.ExpiresAtUtc);
        Assert.Equal(Now.AddMinutes(10), session.AccessExpiresAtUtc);
        Assert.True(session.IsActive(Now));
        Assert.False(session.IsActive(session.ExpiresAtUtc));
    }
    [Fact]
    public void RotationCannotExtendAbsoluteExpiry()
    {
        var session = Create();
        session.RotateAccess(new string('B', 64), session.ExpiresAtUtc.AddMinutes(-1));
        Assert.Equal(session.ExpiresAtUtc, session.AccessExpiresAtUtc);
        Assert.Throws<InvalidOperationException>(() => session.RotateAccess(new string('C', 64), session.ExpiresAtUtc));
    }
    [Fact]
    public void RevocationIsIdempotentAndPreventsRotation()
    {
        var session = Create();
        session.Revoke(Now);
        session.Revoke(Now.AddMinutes(1));
        Assert.Equal(Now, session.RevokedAtUtc);
        Assert.False(session.IsActive(Now));
        Assert.Throws<InvalidOperationException>(() => session.RotateAccess(new string('B', 64), Now));
    }
    [Fact]
    public void RefreshProofCanOnlyBeConsumedOnce()
    {
        var proof = SessionRefreshToken.Create(Guid.NewGuid(), new string('A', 64));
        proof.Consume(Now);
        Assert.Throws<InvalidOperationException>(() => proof.Consume(Now));
    }
    [Theory]
    [InlineData("")]
    [InlineData("plain-secret")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void RejectsNonDigestStorage(string hash) => Assert.Throws<ArgumentException>(() =>
        AuthSession.Create(Guid.NewGuid(), SessionClient.Web, "stamp", hash, Now, false));
}
