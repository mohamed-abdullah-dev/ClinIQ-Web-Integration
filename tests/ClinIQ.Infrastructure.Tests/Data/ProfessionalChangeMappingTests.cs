using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Data;

public sealed class ProfessionalChangeMappingTests
{
    [Fact]
    public void ModelEnforcesSinglePendingOwnershipAndPreservesEvidenceHistory()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=True").Options, new NoOpPublisher());
        var request = db.Model.FindEntityType(typeof(DoctorProfessionalChange))!;
        Assert.Contains(request.GetIndexes(), index => index.IsUnique && index.GetFilter() == "[Status] = 1" &&
            index.Properties.Single().Name == nameof(DoctorProfessionalChange.DoctorProfileId));
        Assert.Contains(request.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(DoctorProfile) && key.Properties.Count == 2);
        Assert.True(request.FindProperty(nameof(DoctorProfessionalChange.Version))!.IsConcurrencyToken);
        Assert.True(db.Model.FindEntityType(typeof(DoctorProfile))!.FindProperty(nameof(DoctorProfile.Version))!.IsConcurrencyToken);
        Assert.All(request.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
        Assert.Equal("binary(8)", request.FindProperty(nameof(DoctorProfessionalChange.OriginalProfileVersion))!.GetColumnType());
    }
    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }
}
