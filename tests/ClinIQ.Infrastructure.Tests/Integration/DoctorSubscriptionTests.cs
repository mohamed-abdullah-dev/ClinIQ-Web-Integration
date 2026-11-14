using System.Security.Claims;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Subscriptions;
using ClinIQ.Domain.Common.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorSubscriptionTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private Task<Result<DoctorSubscriptionDto>> Call(Guid? actor, bool activate = false, string client = "Web") =>
        fixture.AsAsync(actor, services =>
        {
            var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
            (http?.User.Identity as ClaimsIdentity)?.AddClaim(new Claim("client", client));
            var subscriptions = services.GetRequiredService<IDoctorSubscriptionService>();
            return activate ? subscriptions.ActivateOwnProTrialAsync(default) : subscriptions.GetOwnAsync(default);
        });

    [SqlServerFact]
    public async Task ConcurrentRequestsUseOneTrialAndLeaveAnotherDoctorOnStandard()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var initial = (await Call(doctor.UserId)).Value;
        Assert.Equal("Standard", initial.EffectivePlan);
        Assert.True(initial.CanActivateProTrial);
        Assert.Null(initial.Trial);
        Assert.Equal(30, initial.Offer.DurationDays);

        var results = await Task.WhenAll(Call(doctor.UserId, true), Call(doctor.UserId, true));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Value, results[1].Value);
        Assert.Equal("Pro", results[0].Value.EffectivePlan);
        Assert.False(results[0].Value.CanActivateProTrial);
        Assert.Equal(results[0].Value, (await Call(doctor.UserId)).Value);
        Assert.Equal(initial, (await Call(other.UserId)).Value);

        await using var db = fixture.Database.CreateContext();
        var profile = await db.DoctorProfiles.SingleAsync(p => p.UserId == doctor.UserId);
        Assert.Equal(profile.Id, (await db.DoctorProTrials.SingleAsync(t => t.Id == profile.Id)).Id);
    }

    [SqlServerFact]
    public async Task TrialNeedsConfirmedApprovedDoctorWebEligibility()
    {
        var approved = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var unconfirmed = await PracticeLocationTestData.ApprovedDoctor(fixture, confirmed: false);
        var pending = await fixture.DoctorAsync();
        Assert.Equal(DoctorSubscriptionErrors.Unauthorized, Assert.Single((await Call(null, true)).Errors));
        Assert.Equal(DoctorSubscriptionErrors.Forbidden, Assert.Single((await Call(fixture.Admin.Id, true)).Errors));
        Assert.Equal(DoctorSubscriptionErrors.Forbidden, Assert.Single((await Call(pending.Receipt.UserId, true)).Errors));
        Assert.Equal(DoctorSubscriptionErrors.Forbidden, Assert.Single((await Call(unconfirmed.UserId, true)).Errors));
        Assert.Equal(DoctorSubscriptionErrors.Forbidden, Assert.Single((await Call(approved.UserId, true, "Mobile")).Errors));
        Assert.True((await Call(approved.UserId)).Value.CanActivateProTrial);
    }

    [SqlServerFact]
    public async Task ExpiryPreservesBookingAndSchedulingAndPreventsAnotherTrial()
    {
        var scenario = await new AttendanceTests(fixture).Setup();
        await using var db = fixture.Database.CreateContext();
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == scenario.Rows[0].Id);
        var before = await fixture.AsAsync(null, p => p.GetRequiredService<IPublicDirectoryService>().SessionAsync(scenario.Session.Id, default));
        Assert.True(before.IsSuccess);

        var active = (await Call(scenario.Doctor, true)).Value;
        var during = await fixture.AsAsync(null, p => p.GetRequiredService<IPublicDirectoryService>().SessionAsync(scenario.Session.Id, default));
        Assert.Equal(before.Value, during.Value);
        fixture.Database.Clock.UtcNow = active.Trial!.ExpiresAtUtc;
        var expired = (await Call(scenario.Doctor)).Value;
        Assert.Equal("Standard", expired.EffectivePlan);
        Assert.False(expired.CanActivateProTrial);
        Assert.Equal(active.Trial, expired.Trial);
        Assert.Equal(DoctorSubscriptionErrors.TrialUsed, Assert.Single((await Call(scenario.Doctor, true)).Errors));

        var kept = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(appointment.Status, kept.Status);
        Assert.Equal(appointment.Version, kept.Version);
        var scheduling = new SchedulingTests(fixture);
        Assert.True((await scheduling.Extra(scenario.Doctor, scenario.Session.LocationId, scheduling.Tomorrow, 18, 20)).IsSuccess);
    }

    [SqlServerFact]
    public async Task DatabaseProtectsTrialHistoryAndBlocksDestructiveRollback()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture);
        Assert.True((await Call(doctor.UserId, true)).IsSuccess);
        await using var db = fixture.Database.CreateContext();
        var id = await db.DoctorProfiles.Where(p => p.UserId == doctor.UserId).Select(p => p.Id).SingleAsync();
        var mutation = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.DoctorProTrials SET TermsVersion='changed' WHERE DoctorProfileId={id}"));
        var deletion = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE dbo.DoctorProTrials WHERE DoctorProfileId={id}"));
        Assert.Equal(51024, mutation.Number);
        Assert.Equal(51024, deletion.Number);
        var rollback = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261031164704_PracticeLinking"));
        Assert.Equal(51025, rollback.Number);
        Assert.True(await db.DoctorProTrials.AnyAsync(t => t.Id == id));
    }
}
