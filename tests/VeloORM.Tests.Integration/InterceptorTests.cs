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

    [Fact]
    public void Ordered_Paged_ToList_Is_Intercepted()
    {
        var before = _db.QueryCompilationCount;
        // By price: Banana 0.75, Apple 1.50, Cherry 3.00 → Skip(1).Take(2) → Apple, Cherry
        var names = _db.Set<Product>().OrderBy(p => p.Price).Skip(1).Take(2).ToList()
            .Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "Apple", "Cherry" }, names);
        Assert.Equal(before, _db.QueryCompilationCount); // static SQL → no runtime translation
    }

    [Fact]
    public void OrderByDescending_First_Is_Intercepted()
    {
        var before = _db.QueryCompilationCount;
        var top = _db.Set<Product>().OrderByDescending(p => p.Price).First();

        Assert.Equal("Cherry", top.Name);
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Aggregates_Are_Intercepted_With_Correct_Values()
    {
        var before = _db.QueryCompilationCount;
        Assert.Equal(5.25m, _db.Set<Product>().Sum(p => p.Price)); // 1.50 + 0.75 + 3.00
        Assert.Equal(0.75m, _db.Set<Product>().Min(p => p.Price));
        Assert.Equal(3.00m, _db.Set<Product>().Max(p => p.Price));
        Assert.Equal(before, _db.QueryCompilationCount); // all intercepted → zero runtime translation
    }

    [Fact]
    public void Intercepted_Ordered_Equals_Runtime_Ordered()
    {
        var intercepted = _db.Set<Product>().OrderBy(p => p.Price).ToList().Select(p => p.Name).ToArray();
        // Where forces the runtime engine; same ordering must yield the same result.
        var runtime = _db.Set<Product>().Where(p => p.Price >= 0m).OrderBy(p => p.Price).ToList()
            .Select(p => p.Name).ToArray();

        Assert.Equal(runtime, intercepted);
        Assert.True(_db.QueryCompilationCount > 0); // the Where path compiled
    }
}
