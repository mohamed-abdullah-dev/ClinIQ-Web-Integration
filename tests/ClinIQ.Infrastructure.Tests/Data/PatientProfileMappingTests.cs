using ClinIQ.Domain.Patients;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Data;

public sealed class PatientProfileMappingTests
{
    [Fact]
    public void ProfileHasUniqueAccountForeignKeyAuditLinksAndConcurrencyButNoUniquePhone()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=True").Options, new NoOpPublisher());
        var entity = context.Model.FindEntityType(typeof(PatientProfile))!;
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(PatientProfile.UserId));
        Assert.DoesNotContain(entity.GetIndexes(), index => index.IsUnique && index.Properties.Any(p => p.Name == nameof(PatientProfile.PhoneNumber)));
        Assert.Equal(3, entity.GetForeignKeys().Count(fk => fk.PrincipalEntityType.ClrType == typeof(AppUser)));
        Assert.All(entity.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        Assert.True(entity.FindProperty(nameof(PatientProfile.Version))!.IsConcurrencyToken);
        Assert.Equal("rowversion", entity.FindProperty(nameof(PatientProfile.Version))!.GetColumnType());
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
