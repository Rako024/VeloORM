using VeloORM.Data;
using VeloORM.Metadata;
using VeloORM.Migrations;
using VeloORM.Scaffold;
using VeloORM.Sql;

namespace VeloORM.Cli;

/// <summary>
/// The testable core of the <c>velo</c> CLI. Each command operates over a migrations directory and/or
/// a live database; they are thin orchestrations over the Migrations and Scaffold engines.
/// </summary>
public static class CliCommands
{
    /// <summary>Scaffolds a migration from the model vs the current database and saves it. Returns the
    /// migration id, or null when there are no changes.</summary>
    public static string? AddMigration(
        VeloModel model, ISqlDialect dialect, IConnectionFactory factory,
        string migrationsDir, string name, string timestamp, TextWriter output)
    {
        var current = new PostgresSchemaReader(factory).Read();
        var migration = new MigrationScaffolder(dialect).Create(model, current, name, timestamp);

        if (string.IsNullOrWhiteSpace(migration.UpSql))
        {
            output.WriteLine("No schema changes detected; no migration created.");
            return null;
        }

        new MigrationFileStore(migrationsDir).Save(migration);
        output.WriteLine($"Created migration {migration.Id} in {migrationsDir}");
        return migration.Id;
    }

    public static IReadOnlyList<string> UpdateDatabase(IConnectionFactory factory, string migrationsDir, TextWriter output)
    {
        var migrations = new MigrationFileStore(migrationsDir).Load();
        var applied = new Migrator(factory).Update(migrations);
        if (applied.Count == 0)
            output.WriteLine("Database is up to date.");
        else
            foreach (var id in applied)
                output.WriteLine($"Applied {id}");
        return applied;
    }

    public static string? Revert(IConnectionFactory factory, string migrationsDir, TextWriter output)
    {
        var migrator = new Migrator(factory);
        migrator.EnsureHistoryTable();
        var applied = migrator.GetAppliedMigrations();
        if (applied.Count == 0)
        {
            output.WriteLine("Nothing to revert.");
            return null;
        }

        var lastId = applied[^1];
        var migration = new MigrationFileStore(migrationsDir).Load().FirstOrDefault(m => m.Id == lastId)
            ?? throw new InvalidOperationException($"Migration script for '{lastId}' was not found in {migrationsDir}.");

        migrator.RevertMigration(migration);
        output.WriteLine($"Reverted {lastId}");
        return lastId;
    }

    public static void ListMigrations(IConnectionFactory factory, string migrationsDir, TextWriter output)
    {
        var migrator = new Migrator(factory);
        migrator.EnsureHistoryTable();
        var applied = new HashSet<string>(migrator.GetAppliedMigrations());
        var files = new MigrationFileStore(migrationsDir).Load();

        if (files.Count == 0)
        {
            output.WriteLine("No migrations found.");
            return;
        }
        foreach (var m in files)
            output.WriteLine($"[{(applied.Contains(m.Id) ? "X" : " ")}] {m.Id}");
    }

    public static int Scaffold(IConnectionFactory factory, ScaffoldOptions options, string outputDir, TextWriter output)
    {
        var schema = new PostgresSchemaReader(factory).Read();
        var files = new EntityScaffolder(options).Generate(schema);
        Directory.CreateDirectory(outputDir);
        foreach (var (name, source) in files)
            File.WriteAllText(Path.Combine(outputDir, name), source);
        output.WriteLine($"Scaffolded {files.Count} file(s) into {outputDir}");
        return files.Count;
    }
}
