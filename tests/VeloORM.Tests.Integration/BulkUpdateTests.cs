using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class BulkUpdateTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlConnectionFactory _factory = null!;
    private PostgresCommandExecutor _executor = null!;
    private VeloDbContext _db = null!;

    public BulkUpdateTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        _executor = new PostgresCommandExecutor(_factory);
        _db = new VeloDbContext(VeloModel.Build([typeof(Product)]), PostgresDialect.Instance, _factory, _executor);
        await _executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS products CASCADE;
            CREATE TABLE products (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, price numeric NOT NULL,
                in_stock boolean NOT NULL, created_at timestamptz NOT NULL);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTimeOffset Ts = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private void Seed(int n) =>
        _db.BulkInsert(Enumerable.Range(1, n).Select(i => new Product
        {
            Name = "P" + i, Price = i, InStock = true, CreatedAt = Ts,
        }));

    [Fact]
    public void BulkUpdate_Applies_Changes_By_Key_Via_TempTable()
    {
        Seed(500);

        // Read back (with DB-assigned ids), mutate every non-key column.
        var loaded = _db.Set<Product>().ToList();
        foreach (var p in loaded)
        {
            p.Price += 1000m;
            p.Name += "_u";
            p.InStock = false;
        }

        var updated = _db.BulkUpdate(loaded);
        Assert.Equal(500UL, updated);

        var after = _db.Set<Product>().ToList();
        Assert.Equal(500, after.Count);
        Assert.All(after, p => Assert.True(p.Price >= 1001m));
        Assert.All(after, p => Assert.EndsWith("_u", p.Name));
        Assert.All(after, p => Assert.False(p.InStock));
    }

    [Fact]
    public async Task BulkUpdate_In_Transaction_Rolls_Back_When_Not_Committed()
    {
        Seed(50);
        var loaded = _db.Set<Product>().ToList();
        foreach (var p in loaded) p.Price += 1000m;

        await using (var tx = await _db.BeginTransactionAsync())
        {
            _db.BulkUpdate(loaded, tx);
            // No commit: the staged UPDATE must be rolled back on dispose.
        }

        var after = _db.Set<Product>().ToList();
        Assert.All(after, p => Assert.True(p.Price < 1000m)); // unchanged
    }

    [Fact]
    public async Task BulkUpdate_In_Transaction_Persists_On_Commit()
    {
        Seed(50);
        var loaded = _db.Set<Product>().ToList();
        foreach (var p in loaded) p.Price += 1000m;

        await using (var tx = await _db.BeginTransactionAsync())
        {
            var updated = _db.BulkUpdate(loaded, tx);
            Assert.Equal(50UL, updated);
            await tx.CommitAsync();
        }

        var after = _db.Set<Product>().ToList();
        Assert.All(after, p => Assert.True(p.Price >= 1001m));
    }
}
