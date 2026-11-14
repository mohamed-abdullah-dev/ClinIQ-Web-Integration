using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Specialties;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Data.Seeding;

public sealed record SpecialtySeedSummary(int Added, int Existing);

public sealed class SpecialtySeeder(AppDbContext context)
{
    private static readonly (string NameAr, string NameEn)[] InitialCatalog =
    [
        ("طب الأطفال", "Pediatrics"),
        ("الباطنة", "Internal Medicine"),
        ("القلب", "Cardiology"),
        ("الجلدية", "Dermatology"),
        ("العظام", "Orthopedics"),
        ("الأنف والأذن والحنجرة", "Otolaryngology"),
        ("النساء والتوليد", "Obstetrics and Gynecology"),
        ("العيون", "Ophthalmology"),
        ("الجراحة العامة", "General Surgery"),
        ("طب الأسنان", "Dentistry")
    ];

    public async Task<Result<SpecialtySeedSummary>> SeedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Specialty seeding requires a fresh context without pending changes.");

        var candidates = new List<Specialty>();
        foreach (var entry in InitialCatalog)
        {
            var candidate = Specialty.Create(entry.NameAr, entry.NameEn);
            if (candidate.IsError)
                return Result<SpecialtySeedSummary>.Failure(candidate.Errors);
            candidates.Add(candidate.Value);
        }

        if (candidates.Select(row => row.NormalizedNameAr).Distinct(StringComparer.Ordinal).Count() != candidates.Count ||
            candidates.Select(row => row.NormalizedNameEn).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new InvalidOperationException("The initial specialty catalog contains duplicate comparison keys.");

        if ((await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            return SpecialtySeedErrors.MigrationsPending;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Serialize explicit seed runs in this database. The lock ends with the transaction.
        var lockCodes = await context.Database.SqlQueryRaw<int>("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'ClinIQ.Specialties.Seed',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            SELECT @result AS [Value];
            """).ToArrayAsync(cancellationToken);
        var lockCode = lockCodes.Single();
        if (lockCode is -1 or -2 or -3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SpecialtySeedErrors.Busy;
        }
        if (lockCode < 0)
            throw new InvalidOperationException($"Unexpected specialty seed lock error: {lockCode}.");

        var existing = await context.Specialties.AsNoTracking()
            .Select(row => new { row.Id, row.NormalizedNameAr, row.NormalizedNameEn })
            .ToArrayAsync(cancellationToken);
        var byArabic = existing.ToDictionary(row => row.NormalizedNameAr, StringComparer.Ordinal);
        var byEnglish = existing.ToDictionary(row => row.NormalizedNameEn, StringComparer.Ordinal);
        var missing = new List<Specialty>();
        var existingCount = 0;

        foreach (var candidate in candidates)
        {
            byArabic.TryGetValue(candidate.NormalizedNameAr, out var arabicMatch);
            byEnglish.TryGetValue(candidate.NormalizedNameEn, out var englishMatch);
            if (arabicMatch is null && englishMatch is null)
            {
                missing.Add(candidate);
                continue;
            }

            if (arabicMatch is null || englishMatch is null || arabicMatch.Id != englishMatch.Id)
                return SpecialtySeedErrors.NameConflict(candidate.NameAr, candidate.NameEn);

            existingCount++;
        }

        if (missing.Count > 0)
        {
            context.Specialties.AddRange(missing);
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
            {
                return SpecialtySeedErrors.ConcurrentChange;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new SpecialtySeedSummary(missing.Count, existingCount);
    }
}
