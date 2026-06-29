using System.Data.Common;
using VeloORM.Materialization;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;

namespace VeloORM.Tests.Integration;

// Entity used for the Phase 2 read/write proof.
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// A hand-written materializer (no source generator yet) — reflection-free, reads by column name.
internal sealed class ProductMaterializer : IMaterializer<Product>
{
    public Product Read(DbDataReader reader) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Price = reader.GetDecimal(reader.GetOrdinal("price")),
        InStock = reader.GetBoolean(reader.GetOrdinal("in_stock")),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
    };
}

[Collection(PostgresCollection.Name)]
public class RoundTripTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private readonly EntityModel _model = VeloModel.Build([typeof(Product)]).GetEntity<Product>();

    public RoundTripTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _executor = new PostgresCommandExecutor(new NpgsqlConnectionFactory(_fixture.ConnectionString));
    }

    private async Task CreateSchemaAsync()
    {
        const string ddl = """
            DROP TABLE IF EXISTS products;
            CREATE TABLE products (
                id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name        text NOT NULL,
                price       numeric NOT NULL,
                in_stock    boolean NOT NULL,
                created_at  timestamptz NOT NULL
            );
            """;
        await _executor.ExecuteAsync(new SqlStatement(ddl, Array.Empty<SqlParameterBinding>()));
    }

    private QueryModel SelectProducts(string alias = "p")
    {
        var q = new QueryModel(_model.Schema, _model.TableName, alias);
        foreach (var col in _model.Columns)
            q.Select.Add(new SelectItem(new SqlColumn(alias, col.ColumnName, col.ClrType), col.ColumnName));
        return q;
    }

    [Fact]
    public async Task Insert_Then_Select_RoundTrips_All_Column_Types()
    {
        await CreateSchemaAsync();

        var created = new DateTimeOffset(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);
        var insert = new SqlStatement(
            "INSERT INTO products (name, price, in_stock, created_at) VALUES ($1, $2, $3, $4)",
            new SqlParameterBinding[]
            {
                new("Widget", typeof(string)),
                new(19.99m, typeof(decimal)),
                new(true, typeof(bool)),
                new(created, typeof(DateTimeOffset)),
            });

        var affected = await _executor.ExecuteAsync(insert);
        Assert.Equal(1, affected);

        var stmt = SqlBuilder.Build(SelectProducts(), PostgresDialect.Instance);
        var rows = await _executor.QueryAsync(stmt, new ProductMaterializer());

        var product = Assert.Single(rows);
        Assert.True(product.Id > 0);
        Assert.Equal("Widget", product.Name);
        Assert.Equal(19.99m, product.Price);
        Assert.True(product.InStock);
        Assert.Equal(created, product.CreatedAt);
    }

    [Fact]
    public async Task Where_With_Bound_Parameter_Filters_Rows()
    {
        await CreateSchemaAsync();

        async Task InsertAsync(string name, decimal price, bool inStock)
        {
            await _executor.ExecuteAsync(new SqlStatement(
                "INSERT INTO products (name, price, in_stock, created_at) VALUES ($1, $2, $3, now())",
                new SqlParameterBinding[]
                {
                    new(name, typeof(string)),
                    new(price, typeof(decimal)),
                    new(inStock, typeof(bool)),
                }));
        }

        await InsertAsync("cheap", 5m, true);
        await InsertAsync("mid", 50m, true);
        await InsertAsync("pricey", 500m, false);

        var q = SelectProducts();
        q.Where = new SqlBinary(
            new SqlColumn("p", "price", typeof(decimal)),
            SqlBinaryOperator.GreaterThanOrEqual,
            new SqlParameter(50m, typeof(decimal)));
        q.OrderBy.Add(new Ordering(new SqlColumn("p", "price", typeof(decimal)), descending: false));

        var stmt = SqlBuilder.Build(q, PostgresDialect.Instance);
        var rows = await _executor.QueryAsync(stmt, new ProductMaterializer());

        Assert.Equal(2, rows.Count);
        Assert.Equal("mid", rows[0].Name);
        Assert.Equal("pricey", rows[1].Name);
    }

    [Fact]
    public async Task Injection_Attempt_In_Value_Is_Bound_Not_Executed()
    {
        await CreateSchemaAsync();

        // A value that would drop the table IF it were concatenated rather than bound.
        var malicious = "x'); DROP TABLE products;--";
        await _executor.ExecuteAsync(new SqlStatement(
            "INSERT INTO products (name, price, in_stock, created_at) VALUES ($1, 1, true, now())",
            new SqlParameterBinding[] { new(malicious, typeof(string)) }));

        // Table must still exist and contain the literal string as data.
        var q = SelectProducts();
        var rows = await _executor.QueryAsync(SqlBuilder.Build(q, PostgresDialect.Instance), new ProductMaterializer());

        var product = Assert.Single(rows);
        Assert.Equal(malicious, product.Name);
    }
}
