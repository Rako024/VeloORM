using VeloORM.Metadata;
using VeloORM.Migrations;
using VeloORM.Postgres;

namespace VeloORM.Tests.Integration;

// v1 of the entity.
[Table("widgets")]
public class WidgetV1
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

// v2: adds a nullable Sku column and a unique index on Name (same table).
[Table("widgets")]
[Index(nameof(Name), IsUnique = true)]
public class WidgetV2
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string? Sku { get; set; }
}

[Collection(PostgresCollection.Name)]
public class MigrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlConnectionFactory _factory = null!;
    private PostgresSchemaReader _reader = null!;
    private Migrator _migrator = null!;
    private MigrationScaffolder _scaffolder = null!;

    public MigrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        _reader = new PostgresSchemaReader(_factory);
        _migrator = new Migrator(_factory);
        _scaffolder = new MigrationScaffolder(PostgresDialect.Instance);

        // Clean slate.
        var exec = new PostgresCommandExecutor(_factory);
        await exec.ExecuteAsync(new VeloORM.Query.SqlStatement(
            "DROP TABLE IF EXISTS widgets CASCADE; DROP TABLE IF EXISTS __velo_migrations_history CASCADE;",
            Array.Empty<VeloORM.Query.SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void CreateTable_AddColumn_Revert_RoundTrip()
    {
        // --- Migration 1: create the table from the v1 model ---
        var model1 = VeloModel.Build([typeof(WidgetV1)]);
        var m1 = _scaffolder.Create(model1, _reader.Read(), "init", "20260101000000");
        Assert.Contains("CREATE TABLE", m1.UpSql);

        var applied = _migrator.Update([m1]);
        Assert.Equal(new[] { m1.Id }, applied.ToArray());

        var afterCreate = _reader.Read().FindTable(null, "widgets");
        Assert.NotNull(afterCreate);
        Assert.NotNull(afterCreate!.FindColumn("id"));
        Assert.True(afterCreate.FindColumn("id")!.IsIdentity);
        Assert.NotNull(afterCreate.FindColumn("name"));
        Assert.NotNull(afterCreate.FindColumn("price"));
        Assert.Null(afterCreate.FindColumn("sku"));
        Assert.Equal(new[] { "id" }, afterCreate.PrimaryKey.ToArray());

        // Re-running update is idempotent (already applied).
        Assert.Empty(_migrator.Update([m1]));

        // --- Migration 2: evolve to the v2 model (adds sku column + unique index) ---
        var model2 = VeloModel.Build([typeof(WidgetV2)]);
        var m2 = _scaffolder.Create(model2, _reader.Read(), "add_sku", "20260102000000");
        Assert.Contains("ADD COLUMN", m2.UpSql);
        Assert.Contains("CREATE UNIQUE INDEX", m2.UpSql);

        _migrator.ApplyMigration(m2);

        var afterAdd = _reader.Read().FindTable(null, "widgets")!;
        Assert.NotNull(afterAdd.FindColumn("sku"));
        Assert.True(afterAdd.FindColumn("sku")!.IsNullable);
        Assert.Contains(afterAdd.Indexes, i => i.IsUnique);
        Assert.Equal(new[] { m1.Id, m2.Id }, _migrator.GetAppliedMigrations().ToArray());

        // --- Revert migration 2 ---
        _migrator.RevertMigration(m2);

        var afterRevert = _reader.Read().FindTable(null, "widgets")!;
        Assert.Null(afterRevert.FindColumn("sku"));
        Assert.DoesNotContain(afterRevert.Indexes, i => i.IsUnique);
        Assert.Equal(new[] { m1.Id }, _migrator.GetAppliedMigrations().ToArray());
    }

    [Fact]
    public void No_Changes_Produces_Empty_Migration()
    {
        var model = VeloModel.Build([typeof(WidgetV1)]);
        _migrator.Update([_scaffolder.Create(model, _reader.Read(), "init", "20260101000000")]);

        var noop = _scaffolder.Create(model, _reader.Read(), "noop", "20260103000000");
        Assert.True(_scaffolder.IsEmpty(noop));
    }
}
