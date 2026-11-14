using ClinIQ.Domain.Common.Results;

namespace ClinIQ.Infrastructure.Data.Seeding;

public static class SpecialtySeedErrors
{
    public static readonly Error MigrationsPending = Error.Conflict(
        "SpecialtySeed.MigrationsPending", "Apply the pending database migrations before seeding specialties.");

    public static readonly Error Busy = Error.Conflict(
        "SpecialtySeed.Busy", "Another operation holds the specialty seed lock. Retry in a new scope.");

    public static readonly Error ConcurrentChange = Error.Conflict(
        "SpecialtySeed.ConcurrentChange", "Specialty names changed while seeding. Review the catalog and retry in a new scope.");

    public static Error NameConflict(string nameAr, string nameEn) => Error.Conflict(
        "SpecialtySeed.NameConflict",
        $"Seed entry '{nameAr}' / '{nameEn}' conflicts with existing names. Review the catalog; no names were overwritten.");
}
