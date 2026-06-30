using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

[Table("jusers")]
public class JUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<JOrder>? Orders { get; set; }
}

[Table("jorders")]
public class JOrder
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public int UserId { get; set; }
    public JUser? User { get; set; }
}

[Collection(PostgresCollection.Name)]
public class JoinIncludeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VeloDbContext _db = null!;

    public JoinIncludeTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        var executor = new PostgresCommandExecutor(factory);
        _db = new VeloDbContext(VeloModel.Build([typeof(JUser), typeof(JOrder)]),
            PostgresDialect.Instance, factory, executor);

        await executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS jorders CASCADE;
            DROP TABLE IF EXISTS jusers CASCADE;
            CREATE TABLE jusers (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
            CREATE TABLE jorders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                total numeric NOT NULL,
                user_id integer NOT NULL REFERENCES jusers(id));
            INSERT INTO jusers (name) VALUES ('Alice'), ('Bob');
            INSERT INTO jorders (total, user_id) VALUES (10, 1), (20, 1), (5, 2);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Explicit_Join_Projects_Combined_Rows()
    {
        var rows = _db.Set<JOrder>()
            .Join(_db.Set<JUser>(), o => o.UserId, u => u.Id, (o, u) => new { o.Id, o.Total, Buyer = u.Name })
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Buyer)));
        Assert.Equal(2, rows.Count(r => r.Buyer == "Alice"));
        Assert.Equal(35m, rows.Sum(r => r.Total));
    }

    [Fact]
    public void Navigation_Member_In_Where_And_Select_Auto_Joins()
    {
        var aliceOrders = _db.Set<JOrder>()
            .Where(o => o.User!.Name == "Alice")
            .Select(o => new { o.Id, Buyer = o.User!.Name, o.Total })
            .ToList();

        Assert.Equal(2, aliceOrders.Count);
        Assert.All(aliceOrders, r => Assert.Equal("Alice", r.Buyer));
    }

    [Fact]
    public void Include_Reference_Populates_Navigation()
    {
        var orders = _db.Set<JOrder>().Include(o => o.User).ToList();

        Assert.Equal(3, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.User));
        Assert.Contains(orders, o => o.User!.Name == "Bob");
        Assert.Equal("Alice", orders.First(o => o.UserId == 1).User!.Name);
    }

    [Fact]
    public void Include_Collection_Populates_Children_Via_Second_Query()
    {
        var users = _db.Set<JUser>().Include(u => u.Orders).ToList();

        var alice = users.Single(u => u.Name == "Alice");
        var bob = users.Single(u => u.Name == "Bob");

        Assert.NotNull(alice.Orders);
        Assert.Equal(2, alice.Orders!.Count);
        Assert.Equal(30m, alice.Orders!.Sum(o => o.Total));
        Assert.Single(bob.Orders!);
    }

    [Fact]
    public void SingleTable_Still_Works_And_Join_Predicate_Value_Is_Bound()
    {
        // Single-table regression.
        var names = _db.Set<JUser>().Where(u => u.Id > 0).OrderBy(u => u.Name).Select(u => u.Name).ToList();
        Assert.Equal(new[] { "Alice", "Bob" }, names.ToArray());

        // Injection attempt through a navigation predicate stays bound.
        var malicious = "Alice'; DROP TABLE jorders;--";
        var none = _db.Set<JOrder>().Where(o => o.User!.Name == malicious).ToList();
        Assert.Empty(none);
        Assert.Equal(3, _db.Set<JOrder>().Count()); // table intact
    }
}
