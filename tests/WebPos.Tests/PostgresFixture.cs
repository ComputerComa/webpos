using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebPos.Services;
using WebPos.Data;

namespace WebPos.Tests;

/// <summary>
/// Creates a throwaway database on the Postgres server from docker-compose (or the one in
/// WEBPOS_TEST_CONNECTION), applies the real migrations and the startup seeding (roles, default superadmin),
/// and drops it afterwards.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime, IDbContextFactory<PosDbContext>
{
    private readonly DbContextOptions<PosDbContext> _options;

    public PostgresFixture()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("WEBPOS_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Username=webpos;Password=webpos")
        {
            Database = $"webpos_test_{Guid.NewGuid():N}",
        };
        ConnectionString = builder.ConnectionString;
        _options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ConnectionString).Options;
    }

    public string ConnectionString { get; }

    public PosDbContext CreateDbContext() => new(_options);

    public async Task InitializeAsync()
    {
        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDbContext());
        services.AddPosIdentity();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await IdentitySeeder.SeedAsync(scope.ServiceProvider);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
