using System.Data.Common;
using VeloORM.Data;

namespace VeloORM.Migrations;

/// <summary>
/// Applies and reverts migrations against the database, tracking applied ids in
/// <c>__velo_migrations_history</c>. Each migration runs inside a transaction, so a partial failure
/// rolls back cleanly and the history stays consistent.
/// </summary>
public sealed class Migrator
{
    public const string HistoryTable = "__velo_migrations_history";

    private readonly IConnectionFactory _connectionFactory;

    public Migrator(IConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public void EnsureHistoryTable()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        ExecuteNonQuery(connection, null,
            $"CREATE TABLE IF NOT EXISTS {HistoryTable} (" +
            "migration_id text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());");
    }

    public IReadOnlyList<string> GetAppliedMigrations()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        var ids = new List<string>();
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT migration_id FROM {HistoryTable} ORDER BY migration_id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>Applies all migrations whose id is not yet recorded, in id order. Returns the applied ids.</summary>
    public IReadOnlyList<string> Update(IEnumerable<ScriptedMigration> migrations)
    {
        EnsureHistoryTable();
        var applied = new HashSet<string>(GetAppliedMigrations());
        var result = new List<string>();
        foreach (var migration in migrations.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (applied.Contains(migration.Id))
                continue;
            ApplyMigration(migration);
            result.Add(migration.Id);
        }
        return result;
    }

    public void ApplyMigration(ScriptedMigration migration)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (!string.IsNullOrWhiteSpace(migration.UpSql))
                ExecuteNonQuery(connection, transaction, migration.UpSql);
            RecordHistory(connection, transaction, migration.Id, insert: true);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void RevertMigration(ScriptedMigration migration)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (!string.IsNullOrWhiteSpace(migration.DownSql))
                ExecuteNonQuery(connection, transaction, migration.DownSql);
            RecordHistory(connection, transaction, migration.Id, insert: false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void RecordHistory(DbConnection connection, DbTransaction transaction, string id, bool insert)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = insert
            ? $"INSERT INTO {HistoryTable} (migration_id) VALUES ($1);"
            : $"DELETE FROM {HistoryTable} WHERE migration_id = $1;";
        var p = cmd.CreateParameter();
        p.Value = id;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
