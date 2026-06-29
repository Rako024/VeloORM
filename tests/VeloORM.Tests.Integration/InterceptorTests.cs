using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

/// <summary>
/// Verifies the compile-time interceptor layer. When a query shape is fully static
/// (<c>db.Set&lt;T&gt;().Terminal()</c>) the source generator replaces the call with a baked
/// SQL + materializer, so the runtime engine never translates anything — asserted via
/// <see cref="VeloDbContext.QueryCompilationCount"/> staying at zero.
/// </summary>
[Collection(PostgresCollection.Name)]
public class InterceptorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public InterceptorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _executor = new PostgresCommandExecutor(new NpgsqlConnectionFactory(_fixture.ConnectionString));
    }

    public async Task InitializeAsync()
    {
        var model = VeloModel.Build([typeof(Product)]);
        _db = new VeloDbContext(model, PostgresDialect.Instance,
            new NpgsqlConnectionFactory(_fixture.ConnectionString), _executor);

        await _executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS products CASCADE;
            CREATE TABLE products (
                id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name        text NOT NULL,
                price       numeric NOT NULL,
                in_stock    boolean NOT NULL,
                created_at  timestamptz NOT NULL
            );
            INSERT INTO products (name, price, in_stock, created_at) VALUES
                ('Apple', 1.50, true, now()),
                ('Banana', 0.75, true, now()),
                ('Cherry', 3.00, false, now());
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void ToList_Is_Intercepted_No_Runtime_Translation()
    {
        var before = _db.QueryCompilationCount;
        var all = _db.Set<Product>().ToList();

        Assert.Equal(3, all.Count);
        Assert.All(all, p => Assert.True(p.Id > 0 && p.Name.Length > 0));
        // Intercepted path bypasses the runtime engine entirely.
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Count_Is_Intercepted()
    {
        var before = _db.QueryCompilationCount;
        Assert.Equal(3, _db.Set<Product>().Count());
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Any_Is_Intercepted()
    {
        var before = _db.QueryCompilationCount;
        Assert.True(_db.Set<Product>().Any());
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void First_Is_Intercepted_And_Materializes_Correctly()
    {
        var before = _db.QueryCompilationCount;
        var product = _db.Set<Product>().First();

        Assert.True(product.Id > 0);
        Assert.False(string.IsNullOrEmpty(product.Name));
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Interceptor_Result_Equals_Runtime_Result()
    {
        // Intercepted whole-table query...
        var intercepted = _db.Set<Product>().ToList().OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        // ...vs the runtime engine path (Where forces a non-static shape -> runtime).
        var runtime = _db.Set<Product>().Where(p => p.Id > 0).ToList().OrderBy(p => p.Id).Select(p => p.Name).ToArray();

        Assert.Equal(runtime, intercepted);
        // The Where query did go through the engine.
        Assert.True(_db.QueryCompilationCount > 0);
    }
}
