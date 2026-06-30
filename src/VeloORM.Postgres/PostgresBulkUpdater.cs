using System.Diagnostics.CodeAnalysis;
using System.Text;
using Npgsql;
using VeloORM.Data;
using VeloORM.Metadata;

namespace VeloORM.Postgres;

/// <summary>
/// High-throughput bulk update: binary-<c>COPY</c> the rows into a temporary table, then apply them to
/// the target with a single <c>UPDATE … FROM temp</c> join — never a row-by-row <c>UPDATE</c> loop.
/// Rows are matched on the entity's key column(s); every non-key column is updated. Optionally runs
/// inside a caller-supplied transaction.
/// </summary>
public sealed class PostgresBulkUpdater
{
    private readonly IConnectionFactory _connectionFactory;

    public PostgresBulkUpdater(IConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    /// <summary>Bulk-updates <paramref name="rows"/> by key and returns the number of rows copied.</summary>
    [RequiresUnreferencedCode("Reads entity property values via reflection.")]
    public ulong Update<T>(EntityModel model, IEnumerable<T> rows, NpgsqlTransaction? transaction = null)
    {
        if (model.KeyColumns.Count == 0)
            throw new InvalidOperationException($"BulkUpdate requires a key on '{model.ClrType.Name}'.");
        var updateColumns = model.Columns.Where(c => !c.IsKey).ToArray();
        if (updateColumns.Length == 0)
            throw new InvalidOperationException($"BulkUpdate on '{model.ClrType.Name}' has no non-key columns to update.");

        var dialect = PostgresDialect.Instance;
        var target = dialect.QuoteQualifiedName(model.Schema, model.TableName);
        // Unique unqualified name so a pooled connection never collides with a leftover temp table.
        var tempName = "velo_bulk_" + Guid.NewGuid().ToString("n");
        var temp = dialect.QuoteIdentifier(tempName);
        var allColumns = model.Columns; // key + data are all copied into the temp table

        NpgsqlConnection connection;
        bool owned;
        if (transaction is not null)
        {
            connection = (NpgsqlConnection)transaction.Connection!;
            owned = false;
        }
        else
        {
            connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
            connection.Open();
            owned = true;
        }

        try
        {
            // The temp table mirrors the target's columns (LIKE copies definitions but not the identity
            // property, so the key column accepts the supplied values).
            Exec(connection, transaction, $"CREATE TEMP TABLE {temp} (LIKE {target} INCLUDING DEFAULTS)");

            ulong copied;
            var copySql = BuildCopyCommand(temp, allColumns, dialect);
            using (var writer = connection.BeginBinaryImport(copySql))
            {
                BulkCopyWriter.Write(writer, allColumns, rows);
                copied = writer.Complete();
            }

            Exec(connection, transaction, BuildUpdateFrom(target, temp, model.KeyColumns, updateColumns, dialect));
            Exec(connection, transaction, $"DROP TABLE {temp}");
            return copied;
        }
        finally
        {
            if (owned) connection.Dispose();
        }
    }

    private static void Exec(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null) command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static string BuildCopyCommand(string temp, IReadOnlyList<ColumnModel> columns, PostgresDialect dialect)
    {
        var sb = new StringBuilder("COPY ").Append(temp).Append(" (");
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(dialect.QuoteIdentifier(columns[i].ColumnName));
        }
        return sb.Append(") FROM STDIN (FORMAT BINARY)").ToString();
    }

    private static string BuildUpdateFrom(
        string target, string temp,
        IReadOnlyList<ColumnModel> keyColumns, IReadOnlyList<ColumnModel> updateColumns,
        PostgresDialect dialect)
    {
        var sb = new StringBuilder("UPDATE ").Append(target).Append(" AS target SET ");
        for (int i = 0; i < updateColumns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var col = dialect.QuoteIdentifier(updateColumns[i].ColumnName);
            sb.Append(col).Append(" = tmp.").Append(col); // SET left side is an unqualified column
        }
        sb.Append(" FROM ").Append(temp).Append(" AS tmp WHERE ");
        for (int i = 0; i < keyColumns.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            var col = dialect.QuoteIdentifier(keyColumns[i].ColumnName);
            sb.Append("target.").Append(col).Append(" = tmp.").Append(col);
        }
        return sb.ToString();
    }
}
