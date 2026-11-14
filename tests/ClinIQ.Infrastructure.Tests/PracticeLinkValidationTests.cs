using ClinIQ.Application.Features.PracticeLinks;
using ClinIQ.Application.Features.PracticeLinks.Commands;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class PracticeLinkValidationTests
{
    [Theory]
    [InlineData(null, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, true)]
    public void DecisionRequiresExplicitOutcomeAndBothAcceptanceAttestations(bool? accept, bool premises, bool terms, bool valid)
    {
        Assert.Equal(valid, new LinkDecisionValidator().Validate(new LinkDecisionInput(accept, Convert.ToBase64String(new byte[8]), "Reason", premises, terms)).IsValid);
    }
    [Theory]
    [InlineData("")] [InlineData("invalid")] [InlineData("AA==")]
    public void InvalidVersionsCannotReachConsent(string version)
    {
        Assert.False(new LinkCreateValidator().Validate(new LinkCreateInput(Guid.NewGuid(), Guid.NewGuid(), version, version, true, true)).IsValid);
        Assert.False(new LinkDecisionValidator().Validate(new LinkDecisionInput(true, version, "Reason", true, true)).IsValid);
        Assert.False(new LinkWithdrawValidator().Validate(new LinkWithdrawInput(version, "Reason")).IsValid);
    }
}
