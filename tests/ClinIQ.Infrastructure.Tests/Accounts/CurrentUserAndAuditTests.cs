using System.Security.Claims;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Data.Interceptors;
using ClinIQ.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Accounts;

public sealed class CurrentUserAndAuditTests
{
    [Fact]
    public void AnonymousIdentity_CannotSupplyAnActorClaim()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]))
            }
        };
        try { Assert.Null(new CurrentUser(accessor).UserId); }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public void AuthenticatedIdentity_RequiresAValidUserId()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        try
        {
            accessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
            Assert.Throws<InvalidOperationException>(() => new CurrentUser(accessor).UserId);
            var id = Guid.NewGuid();
            accessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));
            Assert.Equal(id, new CurrentUser(accessor).UserId);
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public async Task Audit_UsesVerifiedActor_ProtectsCreator_AndDoesNotCarryActorIntoSystemWork()
    {
        var creator = Guid.NewGuid();
        var modifier = Guid.NewGuid();
        var actor = new MutableActor { UserId = creator };
        var sink = new SuppressSave();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.;Database=NotUsed;Integrated Security=True;TrustServerCertificate=True")
            .AddInterceptors(new AuditableEntityInterceptor(TimeProvider.System, actor), sink).Options;
        await using var context = new AppDbContext(options, new NoOpPublisher());
        var specialty = Specialty.Create("اختبار", "Test").Value;
        specialty.CreatedByUserId = Guid.NewGuid();
        specialty.LastModifiedByUserId = Guid.NewGuid();
        context.Specialties.Add(specialty);
        await context.SaveChangesAsync();
        Assert.Equal(creator, specialty.CreatedByUserId);
        Assert.Null(specialty.LastModifiedByUserId);
        var originalTime = specialty.CreatedAtUtc;
        context.ChangeTracker.AcceptAllChanges();

        actor.UserId = modifier;
        specialty.CreatedByUserId = Guid.NewGuid();
        specialty.CreatedAtUtc = originalTime.AddDays(-1);
        await context.SaveChangesAsync();
        Assert.Equal(creator, specialty.CreatedByUserId);
        Assert.Equal(originalTime, specialty.CreatedAtUtc);
        Assert.Equal(modifier, specialty.LastModifiedByUserId);
        Assert.False(sink.CreationWasModified);
        context.ChangeTracker.AcceptAllChanges();

        actor.UserId = null;
        var systemCreated = Specialty.Create("نظام", "System").Value;
        context.Specialties.Add(systemCreated);
        await context.SaveChangesAsync();
        Assert.Null(systemCreated.CreatedByUserId);
    }

    private sealed class MutableActor : ICurrentUser { public Guid? UserId { get; set; } }
    private sealed class SuppressSave : SaveChangesInterceptor
    {
        public bool CreationWasModified { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CreationWasModified = eventData.Context!.ChangeTracker.Entries<Specialty>()
                .Any(entry => entry.Property(row => row.CreatedByUserId).IsModified || entry.Property(row => row.CreatedAtUtc).IsModified);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }
    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
