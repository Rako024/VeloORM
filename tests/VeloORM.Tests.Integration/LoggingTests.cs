using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

/// <summary>
/// Verifies the static logging hook: <c>db.LogTo(sink)</c> routes each command's parameterized SQL to
/// the sink. Because values are always bound ($N), the logged text never contains them (masking is
/// structural), and a single delegate is stored — no per-query allocation.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LoggingTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public LoggingTests(PostgresFixture fixture)
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
                ('Secret', 9.99, true, now());
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void LogTo_Captures_Parameterized_Sql_And_Masks_Values()
    {
        var log = new List<string>();
        _db.LogTo(log.Add);

        var secretName = "Secret";
        _ = _db.Set<Product>().Where(p => p.Name == secretName).ToList();

        Assert.NotEmpty(log);
        var entry = log.Single(s => s.Contains("FROM") && s.Contains("WHERE"));
        Assert.Contains("$1", entry);                 // value is a bound placeholder
        Assert.DoesNotContain("Secret", entry);       // the value itself is never logged (masked)
        Assert.Contains("param", entry);              // parameter count summary appended
    }

    [Fact]
    public void LogTo_Receives_Whole_Table_And_Aggregate_Sql()
    {
        var log = new List<string>();
        _db.LogTo(log.Add);

        _ = _db.Set<Product>().Where(p => p.Price > 0m).Count();

        Assert.Contains(log, s => s.Contains("count(*)"));
    }
}
