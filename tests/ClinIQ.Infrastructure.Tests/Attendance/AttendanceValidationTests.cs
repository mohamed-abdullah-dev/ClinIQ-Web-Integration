using ClinIQ.Application.Features.Attendance.Commands;
using ClinIQ.Application.Features.Attendance.Queries;
using ClinIQ.Domain.Appointments;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Attendance;

public sealed class AttendanceValidationTests
{
    private static AttendanceInput Valid => new(Guid.NewGuid(), AttendanceAction.CheckIn, Convert.ToBase64String(new byte[8]));
    [Fact]
    public void WritesRequirePatientIdentityVersionValidActionsAndCorrectionFacts()
    {
        var validator = new AttendanceInputValidator(); Assert.True(validator.Validate(Valid).IsValid);
        foreach (var input in new[] { Valid with { ConfirmedPatientId = Guid.Empty }, Valid with { Version = "broken" }, Valid with { Action = (AttendanceAction)99 },
            Valid with { Action = AttendanceAction.Complete }, Valid with { Action = AttendanceAction.NotSeen }, Valid with { Action = AttendanceAction.LateCheckIn, Reason = "Doctor" },
            Valid with { Action = AttendanceAction.Correct, Reason = "Correction", CorrectedStatus = AppointmentStatus.Cancelled }, Valid with { Action = AttendanceAction.Correct, Reason = "Correction", CorrectedStatus = AppointmentStatus.Completed } })
            Assert.False(validator.Validate(input).IsValid);
        Assert.False(new WriteAttendanceValidator().Validate(new WriteAttendanceCommand(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Valid)).IsValid);
    }
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(100001, 20)]
    public void QueueAndHistoryPaginationAreBounded(int page, int size)
    {
        Assert.False(new AttendanceQueueValidator().Validate(new AttendanceQueueQuery(Guid.NewGuid(), Guid.NewGuid(), page, size)).IsValid);
        Assert.False(new AttendanceHistoryValidator().Validate(new AttendanceHistoryQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), page, size)).IsValid);
    }
    [Fact]
    public void SessionListingRequiresADoctorLocationAndShortAscendingDateRange()
    {
        var today = new DateOnly(2026, 11, 1); var validator = new AttendanceSessionsValidator();
        Assert.False(validator.Validate(new AttendanceSessionsQuery(Guid.NewGuid(), Guid.NewGuid(), today, today.AddDays(-1))).IsValid);
        Assert.False(validator.Validate(new AttendanceSessionsQuery(Guid.NewGuid(), Guid.NewGuid(), today, today.AddDays(31))).IsValid);
        Assert.True(validator.Validate(new AttendanceSessionsQuery(Guid.NewGuid(), Guid.NewGuid(), today, today.AddDays(30))).IsValid);
        Assert.False(new AttendancePlacesValidator().Validate(new AttendancePlacesQuery(Guid.Empty)).IsValid);
    }
}
