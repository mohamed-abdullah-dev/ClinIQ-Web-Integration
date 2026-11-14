using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common;

namespace ClinIQ.Domain.PracticeLifecycle;

public sealed class OperationalScopeRecord : Entity
{
    public Guid? LocationId { get; private set; }
    public Guid? BranchId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public int BeforeState { get; private set; }
    public int AfterState { get; private set; }
    public byte[] SourceVersion { get; private set; } = [];
    private OperationalScopeRecord() { }
    private OperationalScopeRecord(Guid id) : base(id) { }
    public static OperationalScopeRecord Create(OperationalScopeKind kind, Guid scope, Guid actor, bool final, string reason, byte[] sourceVersion, DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind) || scope == Guid.Empty || actor == Guid.Empty || sourceVersion.Length != 8 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000)
            throw new ArgumentException("Invalid operational scope history.");
        return new(Guid.NewGuid())
        {
            LocationId = kind == OperationalScopeKind.Location ? scope : null, BranchId = kind == OperationalScopeKind.Branch ? scope : null,
            ActorUserId = actor, Action = final ? "Finalize" : "Begin", Reason = reason.Trim(), RecordedAtUtc = now,
            BeforeState = final ? 2 : 1, AfterState = final ? 3 : 2, SourceVersion = sourceVersion.ToArray()
        };
    }
}

public sealed class EmergencyCancellationRecord : Entity
{
    public Guid RequestId { get; private set; }
    public Guid AppointmentId { get; private set; }
    public byte[] SourceVersion { get; private set; } = [];
    public string BeforeJson { get; private set; } = null!;
    public string AfterJson { get; private set; } = null!;
    public DateTimeOffset RecordedAtUtc { get; private set; }
    private EmergencyCancellationRecord() { }
    private EmergencyCancellationRecord(Guid id) : base(id) { }
    public static EmergencyCancellationRecord Create(OperationalEmergency request, Appointment appointment, byte[] sourceVersion, string before, string after, DateTimeOffset now)
    {
        if (request.Status != EmergencyRequestStatus.Approved || appointment.Status != AppointmentStatus.Cancelled ||
            appointment.CancelledByUserId != request.ReviewerUserId || appointment.CancellationCategory != "AdminEmergencyClosure" || sourceVersion.Length != 8)
            throw new ArgumentException("Emergency cancellation requires an approved decision and cancelled booking.");
        return new(Guid.NewGuid()) { RequestId = request.Id, AppointmentId = appointment.Id, SourceVersion = sourceVersion.ToArray(), BeforeJson = before, AfterJson = after, RecordedAtUtc = now };
    }
}
