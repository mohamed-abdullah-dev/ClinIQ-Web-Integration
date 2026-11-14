using System.Text.RegularExpressions;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Data.Interceptors;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable)))
            Skip = "Set CLINIQ_TEST_SQLSERVER to explicitly run SQL Server integration tests.";
    }
}

public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable)))
            Skip = "Set CLINIQ_TEST_SQLSERVER to explicitly run SQL Server integration tests.";
    }
}

public sealed class SqlServerIntegrationFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "CLINIQ_TEST_SQLSERVER";
    public static readonly DateTimeOffset InitialUtc = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public string DatabaseName { get; } = $"ClinIQ_P01_Tests_{Guid.NewGuid():N}";
    public TestClock Clock { get; } = new();

    private string? _masterConnectionString;
    private DbContextOptions<AppDbContext>? _options;
    private bool _createdHere;

    public async Task InitializeAsync()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configuredConnection)) return;

        var connection = new SqlConnectionStringBuilder(configuredConnection);
        if (!string.IsNullOrEmpty(connection.AttachDBFilename))
            throw new InvalidOperationException("Integration tests do not attach existing database files.");

        connection.InitialCatalog = "master";
        connection.Pooling = false;
        connection.ApplicationName = "ClinIQ.P01.IntegrationTests";
        _masterConnectionString = connection.ConnectionString;

        try
        {
            await using (var master = new SqlConnection(_masterConnectionString))
            {
                await master.OpenAsync();
                using var exists = new SqlCommand("SELECT DB_ID(@name)", master);
                exists.Parameters.AddWithValue("@name", DatabaseName);
                var existingId = await exists.ExecuteScalarAsync();
                if (existingId is not null && existingId != DBNull.Value)
                    throw new InvalidOperationException("Refusing to reuse an existing test database.");

                ValidateOwnedDatabaseName();
                using var create = new SqlCommand($"CREATE DATABASE [{DatabaseName}]", master);
                await create.ExecuteNonQueryAsync();
                _createdHere = true;
                // Express can enable AUTO_CLOSE for a newly created database. These tests deliberately
                // disable pooling, so keep only this fixture-owned database open between connections.
                using var keepOpen = new SqlCommand($"ALTER DATABASE [{DatabaseName}] SET AUTO_CLOSE OFF", master);
                await keepOpen.ExecuteNonQueryAsync();
            }

            connection.InitialCatalog = DatabaseName;
            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(connection.ConnectionString)
                .AddInterceptors(new AuditableEntityInterceptor(Clock,
                    new ClinIQ.Infrastructure.Identity.CurrentUser(new Microsoft.AspNetCore.Http.HttpContextAccessor())))
                .Options;

            await using var context = CreateContext();
            await context.Database.MigrateAsync();
        }
        catch (Exception initializationError)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    $"Test database initialization/cleanup failed: {DatabaseName}.",
                    initializationError, cleanupError);
            }
            throw;
        }
    }

    public AppDbContext CreateContext()
    {
        if (!_createdHere || _options is null)
            throw new InvalidOperationException("SQL Server test database has not been initialized.");

        return new AppDbContext(_options, new NoOpPublisher());
    }

    public async Task DisposeAsync()
    {
        if (!_createdHere) return;
        ValidateOwnedDatabaseName();

        // Only this fixture's freshly created database is eligible for cleanup.
        // Pooling is disabled, and every context must be disposed before teardown.
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        using var drop = new SqlCommand($"DROP DATABASE [{DatabaseName}]", master);
        await drop.ExecuteNonQueryAsync();
        _createdHere = false;
        _options = null;
    }

    private void ValidateOwnedDatabaseName()
    {
        if (!Regex.IsMatch(DatabaseName, "\\AClinIQ_P01_Tests_[0-9a-f]{32}\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Unsafe integration test database name.");
    }

    public sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = InitialUtc;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
