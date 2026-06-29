using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

/// <summary>
/// Exercises the bool-gated optional-filter (fragment) engine: only active fragments are assembled,
/// values are bound, and assembled SQL is cached by the active-fragment bitmask (no 2ⁿ blow-up).
/// </summary>
[Collection(PostgresCollection.Name)]
public class FragmentTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;
    private const string BaseSelect = "SELECT id, name, price, in_stock, created_at FROM products";

    public FragmentTests(PostgresFixture fixture)
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

    private List<Product> Search(string? name, decimal? minPrice, bool? inStock) =>
        _db.FilteredQuery<Product>(BaseSelect)
           .AndWhere(name is not null, $"name = {name}")
           .AndWhere(minPrice.HasValue, $"price >= {minPrice}")
           .AndWhere(inStock.HasValue, $"in_stock = {inStock}")
           .ToList();

    [Fact]
    public void Zero_Filters_Returns_All()
    {
        Assert.Equal(4, Search(null, null, null).Count);
    }

    [Fact]
    public void One_Filter_Applies()
    {
        var byName = Search("Banana", null, null);
        Assert.Equal(new[] { "Banana" }, byName.Select(p => p.Name).ToArray());

        var byPrice = Search(null, 3.00m, null).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Cherry", "Date" }, byPrice);
    }

    [Fact]
    public void Two_And_Three_Filters_Combine()
    {
        var two = Search(null, 1.00m, true).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Apple", "Date" }, two);

        var three = Search("Apple", 1.00m, true).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Apple" }, three);
    }

    [Fact]
    public void Same_Bitmask_Is_Assembled_Once_No_Exponential_Blowup()
    {
        // Warm a couple of distinct combinations.
        _ = Search("Apple", null, null);   // bitmask 001
        _ = Search(null, 1m, true);        // bitmask 110
        var afterWarm = _db.FragmentAssemblyCount;

        // Re-run the SAME two combinations many times with different VALUES: no new assemblies.
        for (int i = 0; i < 5; i++)
        {
            _ = Search("Banana", null, null);   // same 001 shape
            _ = Search(null, 2m, false);        // same 110 shape
        }
        Assert.Equal(afterWarm, _db.FragmentAssemblyCount);

        // A brand-new combination assembles exactly once more.
        _ = Search("Apple", 1m, true); // bitmask 111
        Assert.Equal(afterWarm + 1, _db.FragmentAssemblyCount);
    }

    [Fact]
    public void Fragment_Values_Are_Bound_Not_Concatenated()
    {
        var malicious = "Apple'; DROP TABLE products;--";
        var rows = Search(malicious, null, null);
        Assert.Empty(rows);
        Assert.Equal(4, _db.FilteredQuery<Product>(BaseSelect).ToList().Count); // table intact
    }
}
