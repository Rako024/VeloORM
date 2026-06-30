using VeloORM.Metadata;
using VeloORM.Migrations.Schema;
using VeloORM.Sql;

namespace VeloORM.Migrations;

/// <summary>A migration as two SQL scripts plus a sortable id (<c>{timestamp}_{name}</c>).</summary>
public sealed class ScriptedMigration
{
    public required string Id { get; init; }
    public required string UpSql { get; init; }
    public required string DownSql { get; init; }
}

/// <summary>
/// Produces a <see cref="ScriptedMigration"/> by diffing the desired model schema against the current
/// database schema (Up) and the reverse (Down).
/// </summary>
public sealed class MigrationScaffolder
{
    private readonly ISqlDialect _dialect;

    public MigrationScaffolder(ISqlDialect dialect) => _dialect = dialect;

    public ScriptedMigration Create(VeloModel model, SchemaModel currentDatabase, string name, string timestamp)
    {
        var desired = ModelSchemaBuilder.Build(model, _dialect);
        var generator = new PostgresMigrationSqlGenerator(_dialect);

        var upOps = SchemaDiffer.Diff(currentDatabase, desired);
        var downOps = SchemaDiffer.Diff(desired, currentDatabase);

        var safeName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return new ScriptedMigration
        {
            Id = $"{timestamp}_{safeName}",
            UpSql = generator.Generate(upOps),
            DownSql = generator.Generate(downOps),
        };
    }

    /// <summary>True when the diff produced no operations (model and database already match).</summary>
    public bool IsEmpty(ScriptedMigration migration) => string.IsNullOrWhiteSpace(migration.UpSql);
}
