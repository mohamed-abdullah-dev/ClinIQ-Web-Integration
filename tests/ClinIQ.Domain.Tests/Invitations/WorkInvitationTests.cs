using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLocations;
using Xunit;

namespace ClinIQ.Domain.Tests.Invitations;

public sealed class WorkInvitationTests
{
    [Fact]
    public void WrongAccountCannotConsumeAnInvitationEvenWithTheCorrectEmail()
    {
        var target = Guid.NewGuid(); var row = Invite(target);
        Assert.Equal(InvitationErrors.NotFound, Assert.Single(row.Respond(Guid.NewGuid(), row.NormalizedEmail, true, DateTimeOffset.UnixEpoch).Errors));
        Assert.Equal(WorkInvitationStatus.Pending, row.Status); Assert.Null(row.ClosedByUserId);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecipientDecisionIsFinal(bool accept)
    {
        var target = Guid.NewGuid(); var row = Invite(target);
        Assert.True(row.Respond(target, row.NormalizedEmail, accept, DateTimeOffset.UnixEpoch).IsSuccess);
        Assert.Equal(accept ? WorkInvitationStatus.Accepted : WorkInvitationStatus.Declined, row.Status);
        Assert.Equal(InvitationErrors.Closed, Assert.Single(row.Respond(target, row.NormalizedEmail, !accept, DateTimeOffset.UnixEpoch).Errors));
    }
    [Fact]
    public void ExpiryIsExclusiveAndPreventsAcceptanceAtTheDeadline()
    {
        var target = Guid.NewGuid(); var row = Invite(target);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(7), row.ExpiresAtUtc);
        Assert.Equal(InvitationErrors.Expired, Assert.Single(row.Respond(target, row.NormalizedEmail, true, row.ExpiresAtUtc).Errors));
    }
    [Fact]
    public void RevocationAndReplacementNeverCreateAnAssignment()
    {
        var row = Invite(); Assert.True(row.Close(Guid.NewGuid(), WorkInvitationStatus.Superseded, "Resent", DateTimeOffset.UnixEpoch).IsSuccess);
        Assert.True(ReceptionistAssignment.FromAccepted(row).IsError);
    }
    [Fact]
    public void ReceptionistGrantKeepsExactlyItsInvitationScopeAndRevocationHistory()
    {
        var recipient = Guid.NewGuid(); var row = Invite(recipient); row.Respond(recipient, row.NormalizedEmail, true, DateTimeOffset.UnixEpoch);
        var assignment = ReceptionistAssignment.FromAccepted(row).Value; Assert.Equal(row.BranchId, assignment.BranchId); Assert.Null(assignment.LocationId);
        Assert.Equal(row.Id, assignment.SourceInvitationId); Assert.Equal(recipient, assignment.UserId);
        Assert.True(assignment.Revoke(Guid.NewGuid(), "Ended assignment", DateTimeOffset.UnixEpoch).IsSuccess);
        Assert.False(assignment.IsActive); Assert.Equal("Ended assignment", assignment.RevocationReason);
        Assert.True(assignment.Revoke(Guid.NewGuid(), "Again", DateTimeOffset.UnixEpoch).IsError);
    }
    [Theory]
    [InlineData(WorkInvitationKind.Doctor)]
    [InlineData(WorkInvitationKind.Receptionist)]
    public void FeeBelongsOnlyToDoctorInvitations(WorkInvitationKind kind)
    {
        var row = WorkInvitation.Create(Guid.NewGuid(), kind, Guid.NewGuid(), null, Guid.NewGuid(), "user@example.test", "USER@EXAMPLE.TEST", null, new string('A', 64), "Terms",
            kind == WorkInvitationKind.Doctor ? null : ConsultationPrice.Create(50, "EGP").Value, DateTimeOffset.UnixEpoch);
        Assert.True(row.IsError);
    }
    private static WorkInvitation Invite(Guid? target = null) => WorkInvitation.Create(Guid.NewGuid(), WorkInvitationKind.Receptionist, Guid.NewGuid(), null, Guid.NewGuid(),
        "user@example.test", "USER@EXAMPLE.TEST", target, new string('A', 64), "Terms", null, DateTimeOffset.UnixEpoch).Value;
}
