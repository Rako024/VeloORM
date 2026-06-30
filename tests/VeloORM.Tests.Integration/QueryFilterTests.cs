using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

// [VeloQueryFilter] tells the source generator to defer queries on this entity to the runtime engine,
// which applies the model-level filter (the interceptor cannot see fluent filters).
[VeloQueryFilter]
public class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }
}

[Collection(PostgresCollection.Name)]
public class QueryFilterTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresCommandExecutor _executor;
    private VeloDbContext _db = null!;

    public QueryFilterTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _executor = new PostgresCommandExecutor(new NpgsqlConnectionFactory(_fixture.ConnectionString));
    }

    public async Task InitializeAsync()
    {
        var model = VeloModel.Build([typeof(Widget)],
            configure: b => b.Entity<Widget>().HasQueryFilter(w => !w.IsDeleted));
        _db = new VeloDbContext(model, PostgresDialect.Instance,
            new NpgsqlConnectionFactory(_fixture.ConnectionString), _executor);

        await _executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS widgets CASCADE;
            CREATE TABLE widgets (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, is_deleted boolean NOT NULL);
            INSERT INTO widgets (name, is_deleted) VALUES
                ('a', false), ('b', false), ('c', false), ('x', true), ('y', true);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Whole_Table_Query_Excludes_Soft_Deleted()
    {
        // Widget carries [VeloQueryFilter] so the interceptor defers; the runtime applies !IsDeleted.
        var all = _db.Set<Widget>().ToList();
        Assert.Equal(3, all.Count);
        Assert.All(all, w => Assert.False(w.IsDeleted));
    }

    [Fact]
    public void Filter_Applies_To_Count_Where_And_Aggregates()
    {
        Assert.Equal(3, _db.Set<Widget>().Count());
        Assert.Equal(2, _db.Set<Widget>().Where(w => w.Name != "a").Count()); // b, c (x,y filtered out)
        Assert.True(_db.Set<Widget>().Any(w => w.Name == "a"));
        Assert.False(_db.Set<Widget>().Any(w => w.Name == "x")); // soft-deleted, filtered out
    }

    [Fact]
    public void IgnoreQueryFilters_Returns_All_Rows()
    {
        var all = _db.Set<Widget>().IgnoreQueryFilters().ToList();
        Assert.Equal(5, all.Count);

        Assert.Equal(5, _db.Set<Widget>().IgnoreQueryFilters().Count());
    }
}
