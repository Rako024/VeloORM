using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;

namespace VeloORM.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class BulkInsertTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlConnectionFactory _factory = null!;
    private PostgresCommandExecutor _executor = null!;

    public BulkInsertTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        _executor = new PostgresCommandExecutor(_factory);
        await _executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS products CASCADE;
            CREATE TABLE products (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, price numeric NOT NULL,
                in_stock boolean NOT NULL, created_at timestamptz NOT NULL);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Copy_Bulk_Inserts_All_Rows_Skipping_Identity()
    {
        var model = VeloModel.Build([typeof(Product)]).GetEntity<Product>();
        var rows = Enumerable.Range(1, 1000).Select(i => new Product
        {
            Name = "P" + i,
            Price = i % 100,
            InStock = i % 2 == 0,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }).ToList();

        var inserted = new PostgresBulkInserter(_factory).Copy(model, rows);
        Assert.Equal(1000UL, inserted);

        Assert.Equal(1000L, _executor.ExecuteScalar<long>(
            new SqlStatement("SELECT count(*) FROM products", Array.Empty<SqlParameterBinding>())));

        // Identity column was assigned by the database.
        var minId = _executor.ExecuteScalar<long>(
            new SqlStatement("SELECT min(id) FROM products", Array.Empty<SqlParameterBinding>()));
        Assert.True(minId >= 1);
    }
}
