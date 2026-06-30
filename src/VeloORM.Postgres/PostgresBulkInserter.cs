using System.Diagnostics.CodeAnalysis;
using System.Text;
using Npgsql;
using VeloORM.Data;
using VeloORM.Metadata;

namespace VeloORM.Postgres;

/// <summary>
/// High-throughput bulk insert via PostgreSQL binary <c>COPY</c> (Npgsql's binary importer). Store-
/// generated identity columns are skipped so the database assigns them. Reads property values via
/// reflection (the runtime fallback path).
/// </summary>
public sealed class PostgresBulkInserter
{
    private readonly IConnectionFactory _connectionFactory;

    public PostgresBulkInserter(IConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    /// <summary>Bulk-inserts <paramref name="rows"/> and returns the number of rows written.</summary>
    [RequiresUnreferencedCode("Reads entity property values via reflection.")]
    public ulong Copy<T>(EntityModel model, IEnumerable<T> rows)
    {
        // Columns the application supplies (everything except store-generated identity columns).
        var columns = model.Columns.Where(c => c.StoreGenerated != StoreGenerated.OnAdd).ToArray();
        var copySql = BuildCopyCommand(model, columns);

        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        using var writer = connection.BeginBinaryImport(copySql);
        BulkCopyWriter.Write(writer, columns, rows);
        return writer.Complete();
    }

    private static string BuildCopyCommand(EntityModel model, IReadOnlyList<ColumnModel> columns)
    {
        var dialect = PostgresDialect.Instance;
        var sb = new StringBuilder("COPY ");
        sb.Append(dialect.QuoteQualifiedName(model.Schema, model.TableName)).Append(" (");
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(dialect.QuoteIdentifier(columns[i].ColumnName));
        }
        sb.Append(") FROM STDIN (FORMAT BINARY)");
        return sb.ToString();
    }
}
