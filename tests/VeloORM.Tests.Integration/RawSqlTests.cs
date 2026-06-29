using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class RawSqlTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public RawSqlTests(PostgresFixture fixture)
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

            CREATE OR REPLACE FUNCTION products_over(min_price numeric)
            RETURNS SETOF products LANGUAGE sql AS $$
                SELECT * FROM products WHERE price >= min_price ORDER BY price;
            $$;

            CREATE OR REPLACE VIEW in_stock_products AS
                SELECT * FROM products WHERE in_stock;
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Query_Function_Maps_To_Entity_With_Bound_Parameter()
    {
        decimal min = 1.50m;
        var rows = _db.Query<Product>($"SELECT * FROM products_over({min})");
        Assert.Equal(new[] { "Apple", "Cherry" }, rows.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Query_View_Returns_Entities()
    {
        var rows = _db.Query<Product>($"SELECT * FROM in_stock_products ORDER BY name");
        Assert.Equal(new[] { "Apple", "Banana" }, rows.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ExecuteScalar_Reads_Single_Value()
    {
        var count = _db.ExecuteScalar<long>($"SELECT count(*) FROM products");
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task QueryAsync_Works_With_Interpolated_Handler()
    {
        var name = "Banana";
        var rows = await _db.QueryAsync<Product>($"SELECT * FROM products WHERE name = {name}");
        var product = Assert.Single(rows);
        Assert.Equal(0.75m, product.Price);
    }

    [Fact]
    public void Execute_Runs_NonQuery_With_Bound_Parameters()
    {
        var affected = _db.Execute($"UPDATE products SET price = price + {1.00m} WHERE name = {"Apple"}");
        Assert.Equal(1, affected);
        var price = _db.ExecuteScalar<decimal>($"SELECT price FROM products WHERE name = {"Apple"}");
        Assert.Equal(2.50m, price);
    }

    [Fact]
    public void Interpolated_Value_Is_Bound_Not_Concatenated()
    {
        // If this were concatenated, it would drop the table; bound, it just matches no rows.
        var malicious = "Apple'; DROP TABLE products;--";
        var rows = _db.Query<Product>($"SELECT * FROM products WHERE name = {malicious}");
        Assert.Empty(rows);

        // Table still intact.
        Assert.Equal(3, _db.ExecuteScalar<long>($"SELECT count(*) FROM products"));
    }
}
