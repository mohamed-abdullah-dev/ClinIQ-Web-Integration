using ClinIQ.Domain.Common;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Data.Interceptors;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

internal static class PersistenceChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var clock = new FixedClock();
        var sink = new SuppressSqlSave();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.;Database=NotUsed;Integrated Security=True;TrustServerCertificate=True")
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>()
            .AddInterceptors(new AuditableEntityInterceptor(clock,
                new ClinIQ.Infrastructure.Identity.CurrentUser(new Microsoft.AspNetCore.Http.HttpContextAccessor())), sink)
            .Options;
        var publisher = new CallbackPublisher();

        await using (var context = new AppDbContext(options, publisher))
        {
            var entity = new AuditProbe();
            context.Add(entity);
            var model = context.Model.FindEntityType(typeof(AuditProbe))!;
            check(model.FindProperty(nameof(Entity.DomainEvents)) is null &&
                model.FindNavigation(nameof(Entity.DomainEvents)) is null,
                "DomainEvents excluded from the EF model");

            var events = 0;
            publisher.OnPublish = notification =>
            {
                events++;
                entity.Name = "Changed by handler";
                if (((ProbeEvent)notification).Number == 1) entity.Raise(2);
                return Task.CompletedTask;
            };
            entity.Raise(1);
            await context.SaveChangesAsync();
            check(events == 2 && entity.DomainEvents.Count == 0 && sink.Calls == 1 &&
                sink.LastName == "Changed by handler", "Nested domain events finish before saving tracked changes");
            check(entity.CreatedAtUtc == clock.Now && entity.LastModifiedAtUtc is null,
                "Creation audit uses TimeProvider UTC time");

            context.ChangeTracker.AcceptAllChanges();
            clock.Now = clock.Now.AddHours(1);
            entity.Name = "Edited";
            entity.CreatedAtUtc = clock.Now.AddDays(-5);
            await context.SaveChangesAsync();
            check(entity.LastModifiedAtUtc == clock.Now && !sink.CreatedAtWasModified,
                "Updates stamp modification time and protect persisted creation time");
            try { context.SaveChanges(); throw new Exception("Sync save unexpectedly succeeded"); }
            catch (NotSupportedException) { check(true, "Sync saving cannot bypass event dispatch"); }
        }

        await using (var context = new AppDbContext(options, publisher))
        {
            var entity = new AuditProbe();
            context.Add(entity);
            entity.Raise(1);
            publisher.OnPublish = _ => throw new InvalidOperationException("Handler failure");
            var before = sink.Calls;
            try { await context.SaveChangesAsync(); throw new Exception("Expected handler failure"); }
            catch (InvalidOperationException ex) when (ex.Message == "Handler failure") { }
            check(sink.Calls == before, "Event failure prevents persistence");
            await ExpectInvalidSave(context, check, "Failed context cannot silently retry after events were consumed");
        }

        await using (var context = new AppDbContext(options, publisher))
        {
            var entity = new AuditProbe();
            context.Add(entity);
            entity.Raise(1);
            publisher.OnPublish = async _ => { await context.SaveChangesAsync(); };
            await ExpectInvalidSave(context, check, "Recursive SaveChanges from a handler is rejected");
        }

        await using (var context = new AppDbContext(options, publisher))
        {
            var entity = new AuditProbe();
            context.Add(entity);
            entity.Raise(1);
            publisher.OnPublish = _ => { entity.Raise(1); return Task.CompletedTask; };
            await ExpectInvalidSave(context, check, "Unbounded domain-event cycles are rejected");
        }

        await using (var context = new AppDbContext(options, publisher))
        {
            var entity = new AuditProbe();
            context.Add(entity);
            entity.Raise(1);
            var events = 0;
            publisher.OnPublish = _ => { events++; return Task.CompletedTask; };
            try
            {
                await context.SaveChangesAsync(new CancellationToken(canceled: true));
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { check(events == 0, "Canceled save does not dispatch events"); }
        }
    }

    private static async Task ExpectInvalidSave(AppDbContext context, Action<bool, string> check, string name)
    {
        try { await context.SaveChangesAsync(); }
        catch (InvalidOperationException) { check(true, name); return; }
        throw new Exception(name);
    }
}

public sealed class AuditProbe : AuditableEntity
{
    public AuditProbe() : base(Guid.NewGuid()) { }
    public string Name { get; set; } = "Initial";
    public void Raise(int number) => AddDomainEvent(new ProbeEvent(number));
}

public sealed record ProbeEvent(int Number) : IDomainEvent;

public sealed class ProbeModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        // Add the test entity before AppDbContext applies its real DomainEvents exclusion.
        modelBuilder.Entity<AuditProbe>();
        base.Customize(modelBuilder, context);
    }
}

internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class CallbackPublisher : IPublisher
{
    public Func<object, Task> OnPublish { get; set; } = _ => Task.CompletedTask;
    public Task Publish(object notification, CancellationToken cancellationToken = default) => OnPublish(notification);
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => OnPublish(notification);
}

internal sealed class SuppressSqlSave : SaveChangesInterceptor
{
    public int Calls { get; private set; }
    public string? LastName { get; private set; }
    public bool CreatedAtWasModified { get; private set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Calls++;
        var entry = eventData.Context!.ChangeTracker.Entries<AuditProbe>().Single();
        LastName = entry.Entity.Name;
        CreatedAtWasModified = entry.Property(x => x.CreatedAtUtc).IsModified;
        // Exercise the pipeline without connecting to or changing any database.
        return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
    }
}
