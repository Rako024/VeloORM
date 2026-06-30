using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

/// <summary>
/// Verifies the struct-based transaction wrappers: committed work is visible to a later connection,
/// and disposing/rolling back an uncommitted transaction discards the work.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TransactionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public TransactionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _executor = new PostgresCommandExecutor(new NpgsqlConnectionFactory(_fixture.ConnectionString));
    }

    public async Task InitializeAsync()
    {
        _db = new VeloDbContext(VeloModel.Build([typeof(Product)]), PostgresDialect.Instance,
            new NpgsqlConnectionFactory(_fixture.ConnectionString), _executor);
        await _executor.ExecuteAsync(new SqlStatement(
            "DROP TABLE IF EXISTS tx_test; CREATE TABLE tx_test (id integer PRIMARY KEY);",
            Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // A fresh connection (no ambient transaction) sees only committed state.
    private long Count() => _db.ExecuteScalar<long>($"SELECT count(*) FROM tx_test");

    [Fact]
    public async Task CommitAsync_Persists_Changes()
    {
        await using (var tx = await _db.BeginTransactionAsync())
        {
            await tx.ExecuteAsync($"INSERT INTO tx_test (id) VALUES ({1})");
            await tx.ExecuteAsync($"INSERT INTO tx_test (id) VALUES ({2})");
            await tx.CommitAsync();
        }

        Assert.Equal(2L, Count());
    }

    [Fact]
    public async Task Dispose_Without_Commit_Rolls_Back()
    {
        await using (var tx = await _db.BeginTransactionAsync())
        {
            await tx.ExecuteAsync($"INSERT INTO tx_test (id) VALUES ({1})");
            // No commit: disposing the handle must roll the transaction back.
        }

        Assert.Equal(0L, Count());
    }

    [Fact]
    public async Task Explicit_RollbackAsync_Discards_Changes()
    {
        await using (var tx = await _db.BeginTransactionAsync())
        {
            await tx.ExecuteAsync($"INSERT INTO tx_test (id) VALUES ({7})");
            await tx.RollbackAsync();
        }

        Assert.Equal(0L, Count());
    }

    [Fact]
    public void Sync_Scope_Commit_Persists_And_Uncommitted_Rolls_Back()
    {
        using (var scope = _db.BeginTransaction())
        {
            _executor.Execute(new SqlStatement("INSERT INTO tx_test (id) VALUES (10)",
                Array.Empty<SqlParameterBinding>()), scope.Transaction);
            scope.Commit();
        }
        Assert.Equal(1L, Count());

        using (var scope = _db.BeginTransaction())
        {
            _executor.Execute(new SqlStatement("INSERT INTO tx_test (id) VALUES (11)",
                Array.Empty<SqlParameterBinding>()), scope.Transaction);
            // No Commit: rolled back on Dispose.
        }
        Assert.Equal(1L, Count());
    }
}
