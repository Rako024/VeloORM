using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

/// <summary>
/// Verifies stateless explicit loading: db.Entry(entity).Reference(...)/Collection(...).Load() runs a
/// targeted query and assigns the related data onto the instance. No change tracking is involved.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExplicitLoadingTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VeloDbContext _db = null!;

    public ExplicitLoadingTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        var executor = new PostgresCommandExecutor(factory);
        _db = new VeloDbContext(VeloModel.Build([typeof(JUser), typeof(JOrder), typeof(JAddress)]),
            PostgresDialect.Instance, factory, executor);

        await executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS jorders CASCADE;
            DROP TABLE IF EXISTS jusers CASCADE;
            DROP TABLE IF EXISTS jaddresses CASCADE;
            CREATE TABLE jaddresses (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, city text NOT NULL);
            CREATE TABLE jusers (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, address_id integer REFERENCES jaddresses(id));
            CREATE TABLE jorders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                total numeric NOT NULL, user_id integer NOT NULL REFERENCES jusers(id));
            INSERT INTO jaddresses (city) VALUES ('NYC'), ('LA');
            INSERT INTO jusers (name, address_id) VALUES ('Alice', 1), ('Bob', 2);
            INSERT INTO jorders (total, user_id) VALUES (10, 1), (20, 1), (5, 2);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Reference_Load_Populates_Navigation()
    {
        var order = _db.Set<JOrder>().First(o => o.Total == 10m);
        Assert.Null(order.User); // not loaded yet

        _db.Entry(order).Reference(o => o.User).Load();

        Assert.NotNull(order.User);
        Assert.Equal("Alice", order.User!.Name);
    }

    [Fact]
    public async Task Collection_Load_Populates_Children()
    {
        var alice = _db.Set<JUser>().First(u => u.Name == "Alice");
        Assert.Null(alice.Orders);

        await _db.Entry(alice).Collection(u => u.Orders!).LoadAsync();

        Assert.NotNull(alice.Orders);
        Assert.Equal(2, alice.Orders!.Count);
        Assert.Equal(30m, alice.Orders!.Sum(o => o.Total));
    }

    [Fact]
    public void Reference_Load_With_Null_Key_Yields_Null()
    {
        // A detached instance with no FK value: loading must not query, and must null the reference.
        var user = new JUser { Id = 999, Name = "Detached", AddressId = null };
        _db.Entry(user).Reference(u => u.Address).Load();
        Assert.Null(user.Address);
    }

    [Fact]
    public void Reference_Load_Resolves_Address()
    {
        var bob = _db.Set<JUser>().First(u => u.Name == "Bob");
        _db.Entry(bob).Reference(u => u.Address).Load();
        Assert.Equal("LA", bob.Address!.City);
    }
}
