using Testcontainers.PostgreSql;

namespace VeloORM.Tests.Integration;

/// <summary>
/// Spins up a real PostgreSQL 16 container once per test collection via Testcontainers.
/// Requires Docker to be running.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("velo_test")
        .WithUsername("velo")
        .WithPassword("velo_test_password")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
