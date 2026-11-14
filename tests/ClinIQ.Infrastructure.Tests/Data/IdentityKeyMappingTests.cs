using ClinIQ.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Data;

public sealed class IdentityKeyMappingTests
{
    [Theory]
    [InlineData(typeof(IdentityUserLogin<Guid>))]
    [InlineData(typeof(IdentityUserToken<Guid>))]
    public void CompositePrimaryKeys_FitSqlServerByteLimit(Type entityType)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.;Database=NotUsed;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var context = new AppDbContext(options, new NoOpPublisher());
        var key = context.Model.FindEntityType(entityType)!.FindPrimaryKey()!;

        var maximumBytes = key.Properties.Sum(property =>
        {
            if (property.ClrType == typeof(Guid)) return 16;
            Assert.Equal(typeof(string), property.ClrType);
            Assert.StartsWith("nvarchar(", property.GetColumnType());
            var maxLength = Assert.IsType<int>(property.GetMaxLength());
            return checked(maxLength * 2);
        });

        Assert.InRange(maximumBytes, 1, 900);
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification,
            CancellationToken cancellationToken = default) where TNotification : INotification =>
            Task.CompletedTask;
    }
}
