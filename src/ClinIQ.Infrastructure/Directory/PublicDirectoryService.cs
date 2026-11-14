using ClinIQ.Application.Features.Directory;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using ClinIQ.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.PublicDirectory;

internal sealed class PublicDirectoryService(AppDbContext db, WorkforceAccess access, CairoScheduleClock cairo, TimeProvider clock) : IPublicDirectoryService
{
    // Project only public fields. Managed places use the live, approved branch, not their invitation snapshot.
    private IQueryable<PlaceRow> Places =>
        from p in db.DoctorPracticeLocations.AsNoTracking()
        join d in access.Doctors.AsNoTracking() on new { Id = p.DoctorProfileId, p.UserId } equals new { d.Id, d.UserId }
        join s in db.Specialties on d.SpecialtyId equals s.Id
        join branch in db.ClinicBranches on p.ClinicBranchId equals branch.Id into branches
        from b in branches.DefaultIfEmpty()
        join clinic in db.Clinics on b.ClinicId equals clinic.Id into clinics
        from c in clinics.DefaultIfEmpty()
        where p.IsActive && p.LifecycleState == PracticeLifecycleState.Active && p.Status == PracticeLocationStatus.Approved &&
            (p.ClinicBranchId == null || (b.IsActive && b.LifecycleState == Domain.Clinics.BranchLifecycleState.Open && c.IsActive))
        select new PlaceRow
        {
            Id = p.Id,
            DoctorId = d.Id,
            DoctorName = d.ProfessionalName,
            ReviewAverage = db.VisitReviews.Where(r=>r.DoctorProfileId==d.Id&&!r.Hidden&&db.Appointments.Any(a=>a.Id==r.AppointmentId&&a.Status==Domain.Appointments.AppointmentStatus.Completed&&a.CompletedAtUtc!=null)).Select(r=>(double?)r.Stars).Average(),
            ReviewCount = db.VisitReviews.Count(r=>r.DoctorProfileId==d.Id&&!r.Hidden&&db.Appointments.Any(a=>a.Id==r.AppointmentId&&a.Status==Domain.Appointments.AppointmentStatus.Completed&&a.CompletedAtUtc!=null)),
            Grade = d.ProfessionalGrade,
            SpecialtyId = s.Id,
            SpecialtyAr = s.NameAr,
            SpecialtyEn = s.NameEn,
            BranchId = p.ClinicBranchId,
            ClinicId = p.ClinicBranchId == null ? null : (Guid?)c.Id,
            ClinicName = p.ClinicBranchId == null ? null : c.Name,
            Name = p.ClinicBranchId == null ? p.Name : b.Name,
            Address = p.ClinicBranchId == null ? p.Address : b.Address,
            Latitude = p.ClinicBranchId == null ? p.Latitude : b.Latitude,
            Longitude = p.ClinicBranchId == null ? p.Longitude : b.Longitude,
            Phone = p.ClinicBranchId == null ? null : b.PublicPhone,
            Fee = p.ConsultationFee,
            Currency = p.Currency
        };

    private IQueryable<BookingSession> Sessions(DateTimeOffset now, DateOnly from, DateOnly until) => db.BookingSessions.AsNoTracking().Where(s =>
        s.Status == BookingSessionStatus.Published && !s.BookingPaused && s.AllocatedCount < s.MaxCapacity &&
        s.BookingClosesAtUtc > now && s.EndsAtUtc > now && s.LocalDate >= from && s.LocalDate <= until &&
        !db.ScheduleExceptions.Any(h => h.DoctorProfileId == s.DoctorProfileId && h.IsActive && h.LocalDate == s.LocalDate &&
            (h.LocationId == null || h.LocationId == s.LocationId)));

    private (DateTimeOffset Now, DateOnly From, DateOnly Until, bool Valid) Window(DirectoryWindow f)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, cairo.Zone).DateTime);
        var from = f.From ?? today; var until = f.Until ?? today.AddDays(27);
        return (now, from, until, from >= today && until >= from && until <= today.AddDays(27));
    }

    private static IQueryable<PlaceRow> Filter(IQueryable<PlaceRow> places, DirectoryFilter f)
    {
        if (f.SpecialtyId.HasValue) places = places.Where(p => p.SpecialtyId == f.SpecialtyId.Value);
        if (!string.IsNullOrWhiteSpace(f.Name)) { var name = f.Name.Trim(); places = places.Where(p => p.DoctorName.Contains(name)); }
        if (!string.IsNullOrWhiteSpace(f.Area)) { var area = f.Area.Trim(); places = places.Where(p => p.Address.Contains(area)); }
        if (f.MinFee.HasValue) places = places.Where(p => p.Fee >= f.MinFee.Value);
        if (f.MaxFee.HasValue) places = places.Where(p => p.Fee <= f.MaxFee.Value);
        return places;
    }

    private IQueryable<OptionRow> Options(IQueryable<PlaceRow> places, DirectoryFilter f, DateTimeOffset now, DateOnly from, DateOnly until)
    {
        var sessions = Sessions(now, from, until);
        var options = from p in Filter(places, f)
                      from s in sessions.Where(s => s.LocationId == p.Id && s.DoctorProfileId == p.DoctorId)
                          .OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Id).Take(1)
                      select new OptionRow { Place = p, Session = s, DistanceKm = null };
        if (f.Latitude.HasValue && f.Longitude.HasValue)
        {
            var latitude = (double)f.Latitude.Value * Math.PI / 180;
            var longitude = (double)f.Longitude.Value * Math.PI / 180;
            // SQL Server translates the trig functions. Clamp rounding at coincident/antipodal points.
            var distances = options.Select(o => new
            {
                o.Place,
                o.Session,
                Cosine = o.Place.Latitude.HasValue && o.Place.Longitude.HasValue
                    ? (double?)(Math.Sin(latitude) * Math.Sin((double)o.Place.Latitude.Value * Math.PI / 180) +
                        Math.Cos(latitude) * Math.Cos((double)o.Place.Latitude.Value * Math.PI / 180) *
                        Math.Cos((double)o.Place.Longitude.Value * Math.PI / 180 - longitude)) : null
            });
            options = distances.Select(o => new OptionRow
            {
                Place = o.Place,
                Session = o.Session,
                DistanceKm = o.Cosine.HasValue ? (double?)(6371.0088 * Math.Acos(o.Cosine.Value > 1 ? 1 : o.Cosine.Value < -1 ? -1 : o.Cosine.Value)) : null
            });
        }
        if (f.MaxDistanceKm.HasValue) options = options.Where(o => o.DistanceKm.HasValue && o.DistanceKm <= f.MaxDistanceKm.Value);
        return options;
    }

    private static IOrderedQueryable<OptionRow> Order(IQueryable<OptionRow> rows, DirectorySort sort) => (sort switch
    {
        DirectorySort.PriceAscending => rows.OrderBy(o => o.Place.Fee).ThenBy(o => o.Session.StartsAtUtc),
        DirectorySort.PriceDescending => rows.OrderByDescending(o => o.Place.Fee).ThenBy(o => o.Session.StartsAtUtc),
        DirectorySort.Distance => rows.OrderBy(o => o.DistanceKm == null).ThenBy(o => o.DistanceKm).ThenBy(o => o.Session.StartsAtUtc),
        _ => rows.OrderBy(o => o.Session.StartsAtUtc)
    }).ThenBy(o => o.Place.DoctorId).ThenBy(o => o.Place.Id);

    private async Task<DirectoryPage<DoctorOption>> PageAsync(IQueryable<OptionRow> rows, DirectoryFilter f, DateTimeOffset now, CancellationToken ct)
    {
        var count = await rows.CountAsync(ct);
        var page = await Order(rows, f.Sort).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToArrayAsync(ct);
        return new(page.Select(o => new DoctorOption(Doctor(o.Place), Place(o.Place), Session(o.Session), o.DistanceKm)).ToArray(), count, f.Page, f.PageSize, now);
    }

    public async Task<Result<DirectoryPage<DoctorOption>>> SearchAsync(DirectoryFilter filter, CancellationToken ct)
    {
        var w = Window(filter); if (!w.Valid) return DirectoryErrors.Dates;
        return await PageAsync(Options(Places, filter, w.Now, w.From, w.Until), filter, w.Now, ct);
    }

    public async Task<Result<PublicDoctorProfile>> DoctorAsync(Guid id, DirectoryWindow filter, CancellationToken ct)
    {
        var w = Window(filter); if (!w.Valid) return DirectoryErrors.Dates;
        var doctor = await (from d in access.Doctors.AsNoTracking()
                            join s in db.Specialties on d.SpecialtyId equals s.Id
                            where d.Id == id
                            select new PublicDoctor(d.Id, d.ProfessionalName, d.ProfessionalGrade,
                                new PublicSpecialty(s.Id, s.NameAr, s.NameEn),
                                db.VisitReviews.Where(r=>r.DoctorProfileId==d.Id&&!r.Hidden&&db.Appointments.Any(a=>a.Id==r.AppointmentId&&a.Status==Domain.Appointments.AppointmentStatus.Completed&&a.CompletedAtUtc!=null)).Select(r=>(double?)r.Stars).Average(),
                                db.VisitReviews.Count(r=>r.DoctorProfileId==d.Id&&!r.Hidden&&db.Appointments.Any(a=>a.Id==r.AppointmentId&&a.Status==Domain.Appointments.AppointmentStatus.Completed&&a.CompletedAtUtc!=null)))).SingleOrDefaultAsync(ct);
        if (doctor is null) return DirectoryErrors.NotFound;
        var places = Places.Where(p => p.DoctorId == id);
        var count = await places.CountAsync(ct);
        var sessions = Sessions(w.Now, w.From, w.Until);
        var rows = await (from p in places
                          from s in sessions.Where(s => s.LocationId == p.Id && s.DoctorProfileId == id).OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Id).Take(1).DefaultIfEmpty()
                          orderby p.Id
                          select new { Place = p, Session = s })
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToArrayAsync(ct);
        return new PublicDoctorProfile(doctor, new(rows.Select(r => new PublicLocationSummary(Place(r.Place), r.Session == null ? null : Session(r.Session))).ToArray(), count, filter.Page, filter.PageSize, w.Now));
    }

    public async Task<Result<PublicLocationProfile>> LocationAsync(Guid id, DirectoryWindow filter, CancellationToken ct)
    {
        var w = Window(filter); if (!w.Valid) return DirectoryErrors.Dates;
        var place = await Places.SingleOrDefaultAsync(p => p.Id == id, ct); if (place is null) return DirectoryErrors.NotFound;
        var sessions = Sessions(w.Now, w.From, w.Until).Where(s => s.LocationId == id && s.DoctorProfileId == place.DoctorId);
        var count = await sessions.CountAsync(ct);
        var rows = await sessions.OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Id).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToArrayAsync(ct);
        return new PublicLocationProfile(Doctor(place), Place(place), new(rows.Select(Session).ToArray(), count, filter.Page, filter.PageSize, w.Now));
    }

    public async Task<Result<PublicSessionProfile>> SessionAsync(Guid id, CancellationToken ct)
    {
        var w = Window(new());
        var row = await (from p in Places
                         join s in Sessions(w.Now, w.From, w.Until) on p.Id equals s.LocationId
                         where s.Id == id && s.DoctorProfileId == p.DoctorId
                         select new { Place = p, Session = s }).SingleOrDefaultAsync(ct);
        return row is null ? DirectoryErrors.NotFound : new PublicSessionProfile(Doctor(row.Place), Place(row.Place), Session(row.Session), w.Now);
    }

    private IQueryable<Domain.Clinics.ClinicBranch> Branches => db.ClinicBranches.AsNoTracking().Where(b => b.IsActive && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive));
    private IQueryable<PublicBranch> BranchRows(IQueryable<Domain.Clinics.ClinicBranch> branches, DateTimeOffset now, DateOnly from, DateOnly until)
    {
        var places = Places; var sessions = Sessions(now, from, until);
        return branches.Select(b => new PublicBranch(b.Id, b.ClinicId, b.Name, b.Address, b.Latitude, b.Longitude, b.PublicPhone, b.TimeZoneId,
            places.Where(p => p.BranchId == b.Id && sessions.Any(s => s.LocationId == p.Id && s.DoctorProfileId == p.DoctorId)).Select(p => p.DoctorId).Distinct().Count()));
    }

    public async Task<Result<PublicClinicProfile>> ClinicAsync(Guid id, DirectoryWindow filter, CancellationToken ct)
    {
        var w = Window(filter); if (!w.Valid) return DirectoryErrors.Dates;
        var clinic = await db.Clinics.AsNoTracking().Where(c => c.Id == id && c.IsActive).Select(c => new { c.Id, c.Name, c.Description }).SingleOrDefaultAsync(ct);
        if (clinic is null) return DirectoryErrors.NotFound;
        var branches = Branches.Where(b => b.ClinicId == id); var count = await branches.CountAsync(ct);
        var rows = await BranchRows(branches.OrderBy(b => b.Id).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize), w.Now, w.From, w.Until).ToArrayAsync(ct);
        return new PublicClinicProfile(clinic.Id, clinic.Name, clinic.Description, new(rows, count, filter.Page, filter.PageSize, w.Now));
    }

    public async Task<Result<PublicBranchProfile>> BranchAsync(Guid clinicId, Guid branchId, DirectoryFilter filter, CancellationToken ct)
    {
        var w = Window(filter); if (!w.Valid) return DirectoryErrors.Dates;
        var branch = await BranchRows(Branches.Where(b => b.Id == branchId && b.ClinicId == clinicId), w.Now, w.From, w.Until).SingleOrDefaultAsync(ct);
        if (branch is null) return DirectoryErrors.NotFound;
        var doctors = await PageAsync(Options(Places.Where(p => p.BranchId == branchId), filter, w.Now, w.From, w.Until), filter, w.Now, ct);
        return new PublicBranchProfile(branch, doctors);
    }

    private static PublicDoctor Doctor(PlaceRow p) => new(p.DoctorId, p.DoctorName, p.Grade, new(p.SpecialtyId, p.SpecialtyAr, p.SpecialtyEn), p.ReviewAverage, p.ReviewCount);
    private static PublicPlace Place(PlaceRow p) => new(p.Id, p.DoctorId, p.ClinicId, p.ClinicName, p.BranchId, p.Name, p.Address, p.Latitude, p.Longitude, p.Phone, p.Fee, p.Currency);
    private static PublicSession Session(BookingSession s) => new(s.Id, s.LocationId, s.LocalDate, s.StartsLocal, s.EndsLocal, s.TimeZoneId, s.StartsAtUtc, s.EndsAtUtc, s.BookingClosesAtUtc, s.MaxCapacity - s.AllocatedCount);

    private sealed class OptionRow
    {
        public PlaceRow Place { get; init; } = null!;
        public BookingSession Session { get; init; } = null!;
        public double? DistanceKm { get; init; }
    }
    private sealed class PlaceRow
    {
        public Guid Id { get; init; }
        public Guid DoctorId { get; init; }
        public string DoctorName { get; init; } = null!;
        public double? ReviewAverage { get; init; }
        public int ReviewCount { get; init; }
        public ProfessionalGrade Grade { get; init; }
        public Guid SpecialtyId { get; init; }
        public string SpecialtyAr { get; init; } = null!;
        public string SpecialtyEn { get; init; } = null!;
        public Guid? BranchId { get; init; }
        public Guid? ClinicId { get; init; }
        public string? ClinicName { get; init; }
        public string Name { get; init; } = null!;
        public string Address { get; init; } = null!;
        public decimal? Latitude { get; init; }
        public decimal? Longitude { get; init; }
        public string? Phone { get; init; }
        public decimal Fee { get; init; }
        public string Currency { get; init; } = null!;
    }
}
