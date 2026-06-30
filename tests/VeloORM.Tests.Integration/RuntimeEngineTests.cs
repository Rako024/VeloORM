using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class RuntimeEngineTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public RuntimeEngineTests(PostgresFixture fixture)
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
            DROP TABLE IF EXISTS products;
            CREATE TABLE products (
                id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name        text NOT NULL,
                price       numeric NOT NULL,
                in_stock    boolean NOT NULL,
                created_at  timestamptz NOT NULL
            );
            """, Array.Empty<SqlParameterBinding>()));

        await Seed("Apple", 1.50m, true);
        await Seed("Banana", 0.75m, true);
        await Seed("Cherry", 3.00m, false);
        await Seed("Date", 5.25m, true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task Seed(string name, decimal price, bool inStock) =>
        _executor.ExecuteAsync(new SqlStatement(
            "INSERT INTO products (name, price, in_stock, created_at) VALUES ($1, $2, $3, now())",
            new SqlParameterBinding[] { new(name, typeof(string)), new(price, typeof(decimal)), new(inStock, typeof(bool)) }));

    [Fact]
    public void Where_OrderBy_Returns_Filtered_Sorted_Entities()
    {
        var results = _db.Set<Product>()
            .Where(p => p.Price >= 1.50m)
            .OrderBy(p => p.Price)
            .ToList();

        Assert.Equal(new[] { "Apple", "Cherry", "Date" }, results.Select(p => p.Name).ToArray());
        Assert.All(results, p => Assert.True(p.Id > 0));
    }

    [Fact]
    public void Where_With_Captured_Variable_Binds_Value()
    {
        decimal threshold = 3.00m;
        var results = _db.Set<Product>().Where(p => p.Price > threshold).ToList();
        Assert.Equal(new[] { "Date" }, results.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Boolean_Column_Predicate_And_Negation()
    {
        var inStock = _db.Set<Product>().Where(p => p.InStock).OrderBy(p => p.Name).ToList();
        Assert.Equal(new[] { "Apple", "Banana", "Date" }, inStock.Select(p => p.Name).ToArray());

        var outOfStock = _db.Set<Product>().Where(p => !p.InStock).ToList();
        Assert.Equal(new[] { "Cherry" }, outOfStock.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void StartsWith_Translates_To_Like()
    {
        var results = _db.Set<Product>().Where(p => p.Name.StartsWith("Ba")).ToList();
        Assert.Equal(new[] { "Banana" }, results.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Contains_List_Translates_To_In()
    {
        var names = new[] { "Apple", "Cherry" };
        var results = _db.Set<Product>().Where(p => names.Contains(p.Name)).OrderBy(p => p.Name).ToList();
        Assert.Equal(new[] { "Apple", "Cherry" }, results.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Count_And_Any_Use_Scalar_Terminals()
    {
        Assert.Equal(4, _db.Set<Product>().Count());
        Assert.Equal(3, _db.Set<Product>().Count(p => p.InStock));
        Assert.True(_db.Set<Product>().Any(p => p.Price > 5m));
        Assert.False(_db.Set<Product>().Any(p => p.Price > 100m));
    }

    [Fact]
    public void Aggregate_Terminals_Sum_Min_Max_Average()
    {
        // prices: Apple 1.50, Banana 0.75, Cherry 3.00, Date 5.25
        Assert.Equal(10.50m, _db.Set<Product>().Sum(p => p.Price));
        Assert.Equal(0.75m, _db.Set<Product>().Min(p => p.Price));
        Assert.Equal(5.25m, _db.Set<Product>().Max(p => p.Price));
        Assert.Equal(2.625m, _db.Set<Product>().Average(p => p.Price));

        // With a predicate (in-stock: Apple, Banana, Date)
        Assert.Equal(7.50m, _db.Set<Product>().Where(p => p.InStock).Sum(p => p.Price));
        Assert.Equal(0.75m, _db.Set<Product>().Where(p => p.InStock).Min(p => p.Price));
    }

    [Fact]
    public void Aggregate_Empty_Sequence_Matches_Linq_Semantics()
    {
        // Sum over no rows is 0 (not NULL/throw).
        Assert.Equal(0m, _db.Set<Product>().Where(p => p.Price > 1000m).Sum(p => p.Price));

        // Min/Max over no rows with a non-nullable result throw (as LINQ-to-Objects does).
        Assert.Throws<InvalidOperationException>(
            () => _db.Set<Product>().Where(p => p.Price > 1000m).Min(p => p.Price));
    }

    [Fact]
    public void First_Single_Terminals()
    {
        var first = _db.Set<Product>().OrderBy(p => p.Price).First();
        Assert.Equal("Banana", first.Name);

        var single = _db.Set<Product>().Single(p => p.Name == "Cherry");
        Assert.Equal(3.00m, single.Price);

        Assert.Throws<InvalidOperationException>(() => _db.Set<Product>().Single(p => p.InStock));
        Assert.Null(_db.Set<Product>().FirstOrDefault(p => p.Name == "Nope"));
    }

    [Fact]
    public void Take_Skip_Page_Results()
    {
        var page = _db.Set<Product>().OrderBy(p => p.Name).Skip(1).Take(2).ToList();
        Assert.Equal(new[] { "Banana", "Cherry" }, page.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Scalar_Projection_Selects_Single_Column()
    {
        var names = _db.Set<Product>().OrderBy(p => p.Name).Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Apple", "Banana", "Cherry", "Date" }, names.ToArray());
    }

    [Fact]
    public void Anonymous_Projection_Materializes_Constructor()
    {
        var rows = _db.Set<Product>()
            .Where(p => p.InStock)
            .OrderBy(p => p.Price)
            .Select(p => new { p.Name, p.Price })
            .ToList();

        Assert.Equal(new[] { "Banana", "Apple", "Date" }, rows.Select(r => r.Name).ToArray());
        Assert.Equal(0.75m, rows[0].Price);
    }

    [Fact]
    public void Injection_Attempt_In_Linq_Value_Is_Bound()
    {
        var malicious = "Robert'); DROP TABLE products;--";
        var results = _db.Set<Product>().Where(p => p.Name == malicious).ToList();
        Assert.Empty(results);
        // Table still intact:
        Assert.Equal(4, _db.Set<Product>().Count());
    }

    [Fact]
    public void Same_Shape_Different_Values_Does_Not_Recompile()
    {
        // Warm any shared shapes first.
        _ = _db.Set<Product>().Where(p => p.Price > 0m).ToList();
        var afterFirst = _db.QueryCompilationCount;

        // Re-run the SAME shape with different captured values: no new compilation.
        for (decimal v = 1m; v <= 5m; v++)
            _ = _db.Set<Product>().Where(p => p.Price > v).ToList();

        Assert.Equal(afterFirst, _db.QueryCompilationCount);

        // A genuinely different shape DOES compile once more.
        _ = _db.Set<Product>().Where(p => p.InStock).ToList();
        Assert.Equal(afterFirst + 1, _db.QueryCompilationCount);
    }
}
