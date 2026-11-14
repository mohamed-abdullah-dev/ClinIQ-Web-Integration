using ClinIQ.Infrastructure.Accounts;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class SessionSecretsTests
{
    [Fact]
    public void RandomProofRoundTripsAndDigestMatchesOnlyItsSecret()
    {
        var secret = SessionSecrets.Create();
        var id = Guid.NewGuid();
        Assert.True(SessionSecrets.TryParse(SessionSecrets.Format(id, secret), out var parsed, out var value));
        Assert.Equal(id, parsed);
        Assert.Equal(secret, value);
        Assert.True(SessionSecrets.Matches(secret, SessionSecrets.Hash(secret)));
        Assert.False(SessionSecrets.Matches(SessionSecrets.Create(), SessionSecrets.Hash(secret)));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("00000000000000000000000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("11111111111111111111111111111111.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public void RejectsMalformedProofs(string? value) => Assert.False(SessionSecrets.TryParse(value, out _, out _));
}
