namespace VeloORM.Migrations;

/// <summary>Persists migrations as <c>{id}.up.sql</c> / <c>{id}.down.sql</c> pairs in a directory.</summary>
public sealed class MigrationFileStore
{
    private readonly string _directory;

    public MigrationFileStore(string directory) => _directory = directory;

    public void Save(ScriptedMigration migration)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, migration.Id + ".up.sql"), migration.UpSql);
        File.WriteAllText(Path.Combine(_directory, migration.Id + ".down.sql"), migration.DownSql);
    }

    public IReadOnlyList<ScriptedMigration> Load()
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<ScriptedMigration>();

        var migrations = new List<ScriptedMigration>();
        foreach (var upFile in Directory.GetFiles(_directory, "*.up.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(upFile);
            id = id.Substring(0, id.Length - ".up.sql".Length);
            var downFile = Path.Combine(_directory, id + ".down.sql");
            migrations.Add(new ScriptedMigration
            {
                Id = id,
                UpSql = File.ReadAllText(upFile),
                DownSql = File.Exists(downFile) ? File.ReadAllText(downFile) : "",
            });
        }
        return migrations;
    }

    public ScriptedMigration? Latest() => Load().LastOrDefault();
}
