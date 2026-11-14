using System.Text.Json;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PublicDirectoryTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private SchedulingTests Schedule => new(fixture);
    private Task<T> Read<T>(Func<IPublicDirectoryService, Task<T>> run) => fixture.AsAsync(null, p => run(p.GetRequiredService<IPublicDirectoryService>()));
    private Task<Result<DirectoryPage<DoctorOption>>> Search(DirectoryFilter f) => Read(s => s.SearchAsync(f, default));
    private async Task<(Guid Owner, Guid Place, SessionDto Session, DirectoryFilter Filter)> Setup()
    {
        var (d, p) = await Schedule.Place(); var tag = Guid.NewGuid().ToString("N");
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorProfiles SET ProfessionalName={tag} WHERE UserId={d}");
        var session = (await Schedule.Extra(d, p.Id, Schedule.Tomorrow, 18, 20)).Value;
        return (d, p.Id, session, new DirectoryFilter { Name = tag });
    }

    [SqlServerFact]
    public async Task GuestGetsConcreteIdsPricesAndAvailabilityWithoutPrivateAccountOrEvidenceFields()
    {
        var (d, p, session, f) = await Setup();
        await using var db = fixture.Database.CreateContext(); var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == d);
        var sessionsBefore = await db.AuthSessions.CountAsync();
        var result = (await Search(f)).Value; var option = Assert.Single(result.Items);
        Assert.Equal(p, option.Place.Id); Assert.Equal(session.Id, option.NextSession.Id);
        Assert.Equal(option.Doctor.Id, option.Place.DoctorProfileId); Assert.Equal(100, option.Place.ConsultationFee); Assert.Equal("EGP", option.Place.Currency);
        Assert.Equal(10, option.NextSession.RemainingCapacity); Assert.Equal("Africa/Cairo", option.NextSession.TimeZoneId);
        Assert.Null(option.DistanceKm); Assert.Null(option.Doctor.ReviewAverage); Assert.Equal(0, option.Doctor.ReviewCount);
        var json = JsonSerializer.Serialize(result);
        foreach (var secret in new[] { user.Email!, d.ToString(), "UserId", "Evidence", "LicenseNumber", "Password", "Version", "SourceRule", "CancellationReason", "CreatedBy", "RegistrationId" }) Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sessionsBefore, await db.AuthSessions.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task SameDoctorAtTwoPlacesHasSeparatePricesIdsAndNearestSessionPerPlace()
    {
        var (d, p, session, f) = await Setup(); var second = await Schedule.SecondPlace(d);
        var next = (await Schedule.Extra(d, second.Id, Schedule.Tomorrow.AddDays(1), 18, 20)).Value;
        await Schedule.Extra(d, p, Schedule.Tomorrow.AddDays(2), 18, 20);
        var rows = (await Search(f)).Value.Items; Assert.Equal(2, rows.Count);
        Assert.Equal(session.Id, rows[0].NextSession.Id); Assert.Equal(next.Id, rows[1].NextSession.Id);
        Assert.Equal(rows[0].Doctor.Id, rows[1].Doctor.Id); Assert.NotEqual(rows[0].Place.Id, rows[1].Place.Id);
        Assert.Equal(new decimal[] { 100, 200 }, rows.Select(x => x.Place.ConsultationFee));
        Assert.Equal(second.Id, Assert.Single((await Search(f with { MinFee = 150 })).Value.Items).Place.Id);
        Assert.Equal(p, Assert.Single((await Search(f with { MaxFee = 100 })).Value.Items).Place.Id);
        Assert.Equal(second.Id, (await Search(f with { Sort = DirectorySort.PriceDescending })).Value.Items[0].Place.Id);
        Assert.Equal(p, (await Search(f with { Sort = DirectorySort.PriceAscending })).Value.Items[0].Place.Id);
    }

    [SqlServerFact]
    public async Task FiltersUseRealSpecialtyAddressAndCairoDateWithoutFallback()
    {
        var (d, p, _, f) = await Setup(); var second = await Schedule.SecondPlace(d);
        var later = (await Schedule.Extra(d, second.Id, Schedule.Tomorrow.AddDays(2), 18, 20)).Value;
        Assert.Equal(p, Assert.Single((await Search(f with { SpecialtyId = fixture.SpecialtyId, Area = "Address", From = Schedule.Tomorrow, Until = Schedule.Tomorrow })).Value.Items).Place.Id);
        Assert.Equal(later.Id, Assert.Single((await Search(f with { From = Schedule.Tomorrow.AddDays(2), Until = Schedule.Tomorrow.AddDays(2) })).Value.Items).NextSession.Id);
        foreach (var absent in new[] { f with { SpecialtyId = Guid.NewGuid() }, f with { Area = "No such area" }, f with { MaxFee = 1 }, f with { Name = "Unmatched" + Guid.NewGuid() } })
        { var empty = (await Search(absent)).Value; Assert.Empty(empty.Items); Assert.Equal(0, empty.TotalCount); }
        Assert.Equal(DirectoryErrors.Dates, Assert.Single((await Search(f with { From = Schedule.Cairo.Today.AddDays(-1) })).Errors));
        Assert.Equal(DirectoryErrors.Dates, Assert.Single((await Search(f with { Until = Schedule.Cairo.Today.AddDays(28) })).Errors));
        Assert.Equal(DirectoryErrors.Dates, Assert.Single((await Search(f with { From = Schedule.Tomorrow, Until = Schedule.Cairo.Today })).Errors));
    }

    [SqlServerTheory]
    [InlineData("paused")]
    [InlineData("full")]
    [InlineData("cancelled")]
    [InlineData("cutoff")]
    [InlineData("holiday-location")]
    [InlineData("holiday-doctor")]
    public async Task UnbookableSessionsDisappearButTheApprovedPlaceRemainsAnEmptyProfile(string change)
    {
        var (d, p, session, f) = await Setup(); await using var db = fixture.Database.CreateContext();
        Assert.Single((await Search(f)).Value.Items);
        switch (change)
        {
            case "paused": await Schedule.Edit(d, session, SessionEditKind.Pause); break;
            case "full": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE BookingSessions SET AllocatedCount=MaxCapacity WHERE Id={session.Id}"); break;
            case "cancelled": await Schedule.Edit(d, session, SessionEditKind.Cancel); break;
            case "cutoff": fixture.Database.Clock.UtcNow = session.BookingClosesAtUtc; break;
            default:
                // Retain a published occurrence deliberately, proving read-side holiday filtering as well as write-side cancellation.
                db.ScheduleExceptions.Add(ScheduleException.Create(session.DoctorProfileId, d, change == "holiday-doctor" ? null : p, session.LocalDate, "Synthetic holiday").Value);
                await db.SaveChangesAsync(); break;
        }
        Assert.Empty((await Search(f)).Value.Items);
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.SessionAsync(session.Id, default))).Errors));
        var profile = (await Read(s => s.LocationAsync(p, new(), default))).Value;
        Assert.Empty(profile.Sessions.Items); Assert.Equal(0, profile.Sessions.TotalCount);
    }

    [SqlServerTheory]
    [InlineData("email")]
    [InlineData("role")]
    [InlineData("registration")]
    [InlineData("inactive")]
    public async Task CurrentEligibilityIsCheckedForEveryPublicRead(string change)
    {
        var (d, p, session, f) = await Setup(); await using var db = fixture.Database.CreateContext();
        var doctorId = session.DoctorProfileId;
        switch (change)
        {
            case "email": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET EmailConfirmed=0 WHERE Id={d}"); break;
            case "role": await db.Database.ExecuteSqlInterpolatedAsync($"DELETE ur FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId={d} AND r.NormalizedName='DOCTOR'"); break;
            case "registration": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorRegistrations SET IsCurrent=0 WHERE UserId={d}"); break;
            default: await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET IsActive=0 WHERE Id={p}"); break;
        }
        Assert.Empty((await Search(f)).Value.Items);
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.LocationAsync(p, new(), default))).Errors));
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.SessionAsync(session.Id, default))).Errors));
        var doctor = await Read(s => s.DoctorAsync(doctorId, new(), default));
        if (change == "inactive") Assert.Empty(doctor.Value.Locations.Items);
        else Assert.Equal(DirectoryErrors.NotFound, Assert.Single(doctor.Errors));
    }

    [SqlServerFact]
    public async Task PausedNearestSessionFallsBackOnlyToAnotherRealBookableSessionAtThatPlace()
    {
        var (d, p, first, f) = await Setup(); var next = (await Schedule.Extra(d, p, Schedule.Tomorrow.AddDays(1), 18, 20)).Value;
        await Schedule.Edit(d, first, SessionEditKind.Pause);
        Assert.Equal(next.Id, Assert.Single((await Search(f)).Value.Items).NextSession.Id);
        fixture.Database.Clock.UtcNow = next.StartsAtUtc.AddMinutes(5);
        Assert.Equal(next.Id, Assert.Single((await Search(f)).Value.Items).NextSession.Id);
        fixture.Database.Clock.UtcNow = next.EndsAtUtc;
        Assert.Empty((await Search(f)).Value.Items);
    }

    [SqlServerFact]
    public async Task SqlDistanceFilteringSortingAndStablePaginationHandleMissingCoordinates()
    {
        var (d, first, _, f) = await Setup(); var second = await Schedule.SecondPlace(d); var third = await Schedule.SecondPlace(d);
        await Schedule.Extra(d, second.Id, Schedule.Tomorrow.AddDays(1), 18, 20);
        await Schedule.Extra(d, third.Id, Schedule.Tomorrow.AddDays(2), 18, 20);
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET Latitude=30, Longitude=31 WHERE Id={first}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET Latitude=31, Longitude=31 WHERE Id={second.Id}");
        var geo = f with { Latitude = 30, Longitude = 31, Sort = DirectorySort.Distance };
        var rows = (await Search(geo)).Value.Items;
        Assert.Equal(new[] { first, second.Id, third.Id }, rows.Select(x => x.Place.Id));
        Assert.InRange(rows[0].DistanceKm!.Value, 0, .001); Assert.InRange(rows[1].DistanceKm!.Value, 111, 112); Assert.Null(rows[2].DistanceKm);
        Assert.Equal(first, Assert.Single((await Search(geo with { MaxDistanceKm = 50 })).Value.Items).Place.Id);
        Assert.Empty((await Search(geo with { Latitude = -30, Longitude = -149, MaxDistanceKm = 1 })).Value.Items);
        var all = (await Search(f with { Sort = DirectorySort.PriceAscending })).Value;
        var pages = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        { var result = (await Search(f with { Sort = DirectorySort.PriceAscending, Page = page, PageSize = 1 })).Value; Assert.Equal(3, result.TotalCount); pages.Add(Assert.Single(result.Items).Place.Id); }
        Assert.Equal(all.Items.Select(x => x.Place.Id), pages); Assert.Empty((await Search(f with { Page = 20 })).Value.Items);
    }

    [SqlServerFact]
    public async Task EqualTimeAndPriceSortTiesAreStableAcrossDifferentDoctors()
    {
        var (d, p, first, _) = await Setup(); var (other, otherPlace) = await Schedule.Place();
        var second = (await Schedule.Extra(other, otherPlace.Id, Schedule.Tomorrow, 18, 20)).Value;
        var tag = Guid.NewGuid().ToString("N"); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorProfiles SET ProfessionalName={tag} WHERE UserId={d} OR UserId={other}");
        var f = new DirectoryFilter { Name = tag, PageSize = 1 };
        var a = Assert.Single((await Search(f)).Value.Items); var b = Assert.Single((await Search(f with { Page = 2 })).Value.Items);
        Assert.NotEqual(a.Doctor.Id, b.Doctor.Id);
        for (var i = 0; i < 3; i++) Assert.Equal(a, Assert.Single((await Search(f)).Value.Items));
        Assert.Equal(first.StartsAtUtc, second.StartsAtUtc); Assert.Equal(2, (await Search(f)).Value.TotalCount);
    }

    [SqlServerFact]
    public async Task ProfilesDoNotTurnUnpublishedRulesOrUnapprovedApplicationsIntoAvailability()
    {
        var (d, p) = await Schedule.Place(); await Schedule.Rule(d, p.Id, Schedule.Tomorrow);
        await using var db = fixture.Database.CreateContext(); var doctorId = await db.DoctorProfiles.Where(x => x.UserId == d).Select(x => x.Id).SingleAsync();
        var doctor = (await Read(s => s.DoctorAsync(doctorId, new(), default))).Value;
        Assert.Null(Assert.Single(doctor.Locations.Items).NextSession);
        Assert.Empty((await Read(s => s.LocationAsync(p.Id, new(), default))).Value.Sessions.Items);
        var pending = await fixture.DoctorAsync();
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.DoctorAsync(pending.Receipt.RegistrationId, new(), default))).Errors));
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.LocationAsync(Guid.NewGuid(), new(), default))).Errors));
    }

    [SqlServerFact]
    public async Task ManagedProfilesUseLiveBranchDataAndRespectClinicBranchPairAndDeactivation()
    {
        var (doctor, _, _, f) = await Setup(); var (manager, clinic, branch) = await new PremisesCorrectionTests(fixture).Branch();
        await using var db = fixture.Database.CreateContext(); var email = await db.Users.Where(x => x.Id == doctor).Select(x => x.Email).SingleAsync();
        var invitations = new WorkInvitationTests(fixture); var invite = (await invitations.Issue(manager, WorkInvitationKind.Doctor, branch.Id, null, email!, 375)).Value;
        Assert.True((await invitations.Respond(doctor, invite)).IsSuccess);
        var place = await db.DoctorPracticeLocations.Where(x => x.UserId == doctor && x.ClinicBranchId == branch.Id).Select(x => x.Id).SingleAsync();
        await Schedule.Extra(doctor, place, Schedule.Tomorrow.AddDays(1), 18, 20);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicBranches SET Name='Live branch', Address='Live public address', Latitude=30, Longitude=31 WHERE Id={branch.Id}");
        var row = Assert.Single((await Search(f with { Area = "Live public" })).Value.Items);
        Assert.Equal("Live branch", row.Place.Name); Assert.Equal(clinic, row.Place.ClinicId); Assert.Equal(branch.Id, row.Place.BranchId); Assert.Equal(branch.PublicPhone, row.Place.PublicPhone); Assert.Equal(375, row.Place.ConsultationFee);
        var profile = (await Read(s => s.ClinicAsync(clinic, new(), default))).Value; Assert.Equal(1, Assert.Single(profile.Branches.Items).BookableDoctorCount);
        var branchProfile = (await Read(s => s.BranchAsync(clinic, branch.Id, f, default))).Value; Assert.Equal(place, Assert.Single(branchProfile.Doctors.Items).Place.Id);
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.BranchAsync(Guid.NewGuid(), branch.Id, f, default))).Errors));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicBranches SET IsActive=0 WHERE Id={branch.Id}");
        Assert.Empty((await Search(f with { Area = "Live public" })).Value.Items);
        Assert.Empty((await Read(s => s.ClinicAsync(clinic, new(), default))).Value.Branches.Items);
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.LocationAsync(place, new(), default))).Errors));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicBranches SET IsActive=1 WHERE Id={branch.Id}; UPDATE Clinics SET IsActive=0 WHERE Id={clinic}");
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.ClinicAsync(clinic, new(), default))).Errors));
        Assert.Equal(DirectoryErrors.NotFound, Assert.Single((await Read(s => s.BranchAsync(clinic, branch.Id, f, default))).Errors));
        Assert.DoesNotContain((await Search(f)).Value.Items, x => x.Place.Id == place);
    }
}
