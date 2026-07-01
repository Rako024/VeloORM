using VeloORM.Cli;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Scaffold;

namespace VeloORM.Tests.Integration;

[Table("cli_widgets")]
public class CliWidget
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

[Collection(PostgresCollection.Name)]
public class CliTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlConnectionFactory _factory = null!;
    private string _migrationsDir = null!;

    public CliTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        _migrationsDir = Path.Combine(Path.GetTempPath(), "velo_cli_" + Guid.NewGuid().ToString("N"));

        var exec = new PostgresCommandExecutor(_factory);
        await exec.ExecuteAsync(new SqlStatement(
            "DROP TABLE IF EXISTS cli_widgets CASCADE; DROP TABLE IF EXISTS __velo_migrations_history CASCADE;",
            Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_migrationsDir)) Directory.Delete(_migrationsDir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void AddMigration_Update_List_Revert_Lifecycle()
    {
        var model = VeloModel.Build([typeof(CliWidget)]);
        var sw = new StringWriter();

        // add-migration
        var id = CliCommands.AddMigration(model, PostgresDialect.Instance, _factory, _migrationsDir, "init", "20260101000000", sw);
        Assert.NotNull(id);
        Assert.True(File.Exists(Path.Combine(_migrationsDir, id + ".up.sql")));

        // update-database
        var applied = CliCommands.UpdateDatabase(_factory, _migrationsDir, sw);
        Assert.Equal(new[] { id }, applied.ToArray());

        // table now exists
        var reader = new VeloORM.Migrations.PostgresSchemaReader(_factory);
        Assert.NotNull(reader.Read().FindTable(null, "cli_widgets"));

        // list-migrations shows it applied
        var listWriter = new StringWriter();
        CliCommands.ListMigrations(_factory, _migrationsDir, listWriter);
        Assert.Contains($"[X] {id}", listWriter.ToString());

        // revert
        var reverted = CliCommands.Revert(_factory, _migrationsDir, sw);
        Assert.Equal(id, reverted);
        Assert.Null(reader.Read().FindTable(null, "cli_widgets"));
    }

    [Fact]
    public async Task Scaffold_Writes_Entity_Files()
    {
        var exec = new PostgresCommandExecutor(_factory);
        await exec.ExecuteAsync(new SqlStatement(
            "CREATE TABLE cli_widgets (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, title text NOT NULL);",
            Array.Empty<SqlParameterBinding>()));

        var outDir = Path.Combine(_migrationsDir, "scaffold");
        var sw = new StringWriter();
        var count = CliCommands.Scaffold(_factory, new ScaffoldOptions { Namespace = "Gen", ContextName = "GenCtx" }, outDir, sw);

        Assert.True(count >= 2); // entity + context
        Assert.True(File.Exists(Path.Combine(outDir, "CliWidget.cs")));
        Assert.Contains("public class CliWidget", File.ReadAllText(Path.Combine(outDir, "CliWidget.cs")));
    }

    [Fact]
    public async Task Scaffold_Skips_Existing_Files_Unless_Force()
    {
        var exec = new PostgresCommandExecutor(_factory);
        await exec.ExecuteAsync(new SqlStatement(
            "CREATE TABLE cli_widgets (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, title text NOT NULL);",
            Array.Empty<SqlParameterBinding>()));

        var outDir = Path.Combine(_migrationsDir, "scaffold");
        var options = new ScaffoldOptions { Namespace = "Gen", ContextName = "GenCtx" };

        // First run writes the files.
        CliCommands.Scaffold(_factory, options, outDir, new StringWriter());
        var entityPath = Path.Combine(outDir, "CliWidget.cs");
        File.WriteAllText(entityPath, "// hand-edited");

        // Without --force, existing files are preserved.
        var sw = new StringWriter();
        CliCommands.Scaffold(_factory, options, outDir, sw, force: false);
        Assert.Equal("// hand-edited", File.ReadAllText(entityPath));
        Assert.Contains("Skipped existing", sw.ToString());

        // With --force, they are overwritten.
        CliCommands.Scaffold(_factory, options, outDir, new StringWriter(), force: true);
        Assert.Contains("public class CliWidget", File.ReadAllText(entityPath));
    }
}
