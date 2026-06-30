using VeloORM.Migrations;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Scaffold;

namespace VeloORM.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class ScaffoldTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlConnectionFactory _factory = null!;

    public ScaffoldTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        var exec = new PostgresCommandExecutor(_factory);
        // The scaffolder reflects over every table in the public schema. Other test classes in this
        // collection share the database, so clear it first to keep this test order-independent.
        await exec.ExecuteAsync(new SqlStatement("""
            DO $$ DECLARE r record; BEGIN
              FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
                EXECUTE 'DROP TABLE IF EXISTS public.' || quote_ident(r.tablename) || ' CASCADE';
              END LOOP;
            END $$;
            """, Array.Empty<SqlParameterBinding>()));
        await exec.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS customers CASCADE;
            CREATE TABLE customers (
                id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                full_name   text NOT NULL,
                email       text,
                balance     numeric NOT NULL,
                created_at  timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX ix_customers_email ON customers (email);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Scaffold_Generates_Entity_With_Correct_Types_And_Attributes()
    {
        var schema = new PostgresSchemaReader(_factory).Read();
        var scaffolder = new EntityScaffolder(new ScaffoldOptions { Namespace = "Demo", ContextName = "DemoContext" });
        var files = scaffolder.Generate(schema);

        Assert.True(files.ContainsKey("Customer.cs"));
        var entity = files["Customer.cs"];

        Assert.Contains("namespace Demo;", entity);
        Assert.Contains("public class Customer", entity);
        Assert.DoesNotContain("[Table(", entity);              // "customers" matches the convention
        Assert.Contains("[Key]", entity);
        Assert.Contains("public int Id { get; set; }", entity);
        Assert.Contains("public string FullName { get; set; }", entity);
        Assert.Contains("public string? Email { get; set; }", entity);
        Assert.Contains("public decimal Balance { get; set; }", entity);
        Assert.Contains("public System.DateTimeOffset CreatedAt { get; set; }", entity);
        Assert.DoesNotContain("[Column(", entity);             // all names match the convention
    }

    [Fact]
    public void Scaffold_Generates_Context_With_Query_Properties()
    {
        var schema = new PostgresSchemaReader(_factory).Read();
        var files = new EntityScaffolder(new ScaffoldOptions { Namespace = "Demo", ContextName = "DemoContext" }).Generate(schema);

        Assert.True(files.ContainsKey("DemoContext.cs"));
        var context = files["DemoContext.cs"];
        Assert.Contains("public class DemoContext : VeloDbContext", context);
        Assert.Contains("VeloModel.Build(new[] { typeof(Customer) })", context);
        Assert.Contains("public IQueryable<Customer> Customers => Set<Customer>();", context);
    }
}
