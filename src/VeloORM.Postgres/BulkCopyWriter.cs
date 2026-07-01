using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using VeloORM.Metadata;

namespace VeloORM.Postgres;

/// <summary>Shared binary-<c>COPY</c> row writer used by bulk insert and bulk update. Reads property
/// values via reflection (the runtime path) and writes each column with its mapped
/// <see cref="NpgsqlDbType"/> so the server never has to infer types.</summary>
internal static class BulkCopyWriter
{
    [RequiresUnreferencedCode("Reads entity property values via reflection.")]
    public static void Write<T>(NpgsqlBinaryImporter writer, IReadOnlyList<ColumnModel> columns, IEnumerable<T> rows)
    {
        foreach (var row in rows)
        {
            writer.StartRow();
            foreach (var column in columns)
            {
                var value = column.Property.GetValue(row);
                if (value is null)
                {
                    writer.WriteNull();
                    continue;
                }
                if (value is Enum)
                {
                    var underlying = Enum.GetUnderlyingType(value.GetType());
                    writer.Write(Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture), NpgsqlDbType.Integer);
                    continue;
                }
                // Relabel DateTime as Unspecified so COPY accepts it into a `timestamp` column
                // (mirrors PostgresCommandExecutor.BindValue).
                if (value is DateTime dt)
                {
                    writer.Write(dt.Kind == DateTimeKind.Unspecified ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), NpgsqlDbType.Timestamp);
                    continue;
                }
                if (PostgresTypeMapper.GetNpgsqlDbType(column.ClrType) is { } dbType)
                    writer.Write(value, dbType);
                else
                    writer.Write(value);
            }
        }
    }
}
