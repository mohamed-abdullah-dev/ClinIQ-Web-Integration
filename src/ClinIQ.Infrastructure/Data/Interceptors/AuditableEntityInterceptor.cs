using ClinIQ.Domain.Common;
using ClinIQ.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClinIQ.Infrastructure.Data.Interceptors;

public sealed class AuditableEntityInterceptor(TimeProvider timeProvider, ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        UpdateEntities(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        UpdateEntities(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void UpdateEntities(DbContext? context)
    {
        if (context is null) return;
        var utcNow = timeProvider.GetUtcNow();
        var actor = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = utcNow;
                entry.Entity.CreatedByUserId = actor;
                entry.Entity.LastModifiedAtUtc = null;
                entry.Entity.LastModifiedByUserId = null;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Property(entity => entity.CreatedAtUtc).CurrentValue = entry.Property(entity => entity.CreatedAtUtc).OriginalValue;
                entry.Property(entity => entity.CreatedByUserId).CurrentValue = entry.Property(entity => entity.CreatedByUserId).OriginalValue;
                entry.Property(entity => entity.CreatedAtUtc).IsModified = false;
                entry.Property(entity => entity.CreatedByUserId).IsModified = false;
                entry.Entity.LastModifiedAtUtc = utcNow;
                entry.Entity.LastModifiedByUserId = actor;
            }
        }
    }
}
