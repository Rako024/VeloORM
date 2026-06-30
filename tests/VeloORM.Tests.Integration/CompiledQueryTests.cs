using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;
using VeloQuery = VeloORM.Runtime.Query; // disambiguate from the VeloORM.Query namespace

namespace VeloORM.Tests.Integration;

/// <summary>
/// Verifies Phase 15c: parameterized <see cref="Query.Compile"/> queries are intercepted at compile
/// time (baked SQL + typed, boxing-free parameter binding), so invoking the compiled delegate does no
/// runtime translation (<see cref="VeloDbContext.QueryCompilationCount"/> stays put). Unsupported
/// shapes fall back to the runtime engine and still return correct results.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CompiledQueryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public CompiledQueryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _executor = new PostgresCommandExecutor(new NpgsqlConnectionFactory(_fixture.ConnectionString));
    }

    public async Task InitializeAsync()
    {
        _db = new VeloDbContext(VeloModel.Build([typeof(Product)]), PostgresDialect.Instance,
            new NpgsqlConnectionFactory(_fixture.ConnectionString), _executor);
        await _executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS products CASCADE;
            CREATE TABLE products (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, price numeric NOT NULL,
                in_stock boolean NOT NULL, created_at timestamptz NOT NULL);
            INSERT INTO products (name, price, in_stock, created_at) VALUES
                ('Apple', 1.50, true, now()),
                ('Banana', 0.75, true, now()),
                ('Cherry', 3.00, false, now()),
                ('Date', 5.25, true, now());
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Compiled_Where_Parameter_Is_Intercepted_And_Correct()
    {
        var byMinPrice = VeloQuery.Compile<VeloDbContext, decimal, List<Product>>(
            (db, min) => db.Set<Product>().Where(p => p.Price >= min).ToList());

        var before = _db.QueryCompilationCount;

        var names = byMinPrice(_db, 1.50m).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Apple", "Cherry", "Date" }, names);

        // Re-invoke with a different value: same compiled delegate, still no runtime translation.
        var fewer = byMinPrice(_db, 3.00m).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Cherry", "Date" }, fewer);

        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Compiled_Two_Parameters_And_Boolean_Column()
    {
        var q = VeloQuery.Compile<VeloDbContext, decimal, decimal, List<Product>>(
            (db, lo, hi) => db.Set<Product>().Where(p => p.Price >= lo && p.Price <= hi && p.InStock).ToList());

        var before = _db.QueryCompilationCount;
        var names = q(_db, 1m, 4m).Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Apple" }, names); // in (1..4] and in stock: Apple 1.50 (Cherry 3.00 not in stock)
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Compiled_String_Equality_Parameter()
    {
        var byName = VeloQuery.Compile<VeloDbContext, string, Product?>(
            (db, name) => db.Set<Product>().Where(p => p.Name == name).FirstOrDefault());

        var before = _db.QueryCompilationCount;
        Assert.Equal(3.00m, byName(_db, "Cherry")!.Price);
        Assert.Null(byName(_db, "Nope"));
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Compiled_Count_With_Parameter()
    {
        var countAbove = VeloQuery.Compile<VeloDbContext, decimal, int>(
            (db, min) => db.Set<Product>().Count());
        // Count() ignores the parameter here, but the parameter signature still binds.
        var before = _db.QueryCompilationCount;
        Assert.Equal(4, countAbove(_db, 0m));
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Compiled_Ordered_Take_Parameter_Pagination()
    {
        var topN = VeloQuery.Compile<VeloDbContext, int, List<Product>>(
            (db, n) => db.Set<Product>().OrderByDescending(p => p.Price).Take(n).ToList());

        var before = _db.QueryCompilationCount;
        var names = topN(_db, 2).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Date", "Cherry" }, names); // 5.25, 3.00
        Assert.Equal(before, _db.QueryCompilationCount);
    }

    [Fact]
    public void Unsupported_Compiled_Shape_Falls_Back_To_Runtime()
    {
        // String.Contains in the predicate is outside the compiled grammar → identity delegate → runtime.
        var search = VeloQuery.Compile<VeloDbContext, string, List<Product>>(
            (db, term) => db.Set<Product>().Where(p => p.Name.Contains(term)).ToList());

        var before = _db.QueryCompilationCount;
        var names = search(_db, "a").Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Banana", "Date" }, names); // names containing 'a'
        Assert.True(_db.QueryCompilationCount > before); // went through the runtime engine
    }
}
