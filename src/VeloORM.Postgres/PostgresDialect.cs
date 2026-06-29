using System.Text;
using VeloORM.Metadata;
using VeloORM.Sql;

namespace VeloORM.Postgres;

/// <summary>PostgreSQL implementation of <see cref="ISqlDialect"/> (the only fully-supported v1 dialect).</summary>
public sealed class PostgresDialect : ISqlDialect
{
    public static readonly PostgresDialect Instance = new();

    public string Name => "postgres";

    public string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string QuoteQualifiedName(string? schema, string name) =>
        string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(name)
            : QuoteIdentifier(schema!) + "." + QuoteIdentifier(name);

    public string RenderParameter(int ordinalZeroBased) =>
        "$" + (ordinalZeroBased + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void AppendPaging(StringBuilder sql, int? limit, int? offset)
    {
        if (limit is { } l)
            sql.Append(" LIMIT ").Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (offset is { } o)
            sql.Append(" OFFSET ").Append(o.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool SupportsReturning => true;

    public string GetStoreType(ColumnModel column) => PostgresTypeMapper.GetStoreType(column);

    public string BuildUpsert(EntityModel entity, IReadOnlyList<ColumnModel> insertColumns)
    {
        if (!entity.HasKey)
            throw new InvalidOperationException(
                $"Cannot build upsert for '{entity.ClrType.Name}': no primary key is defined.");

        var sb = new StringBuilder(128);
        sb.Append("INSERT INTO ").Append(QuoteQualifiedName(entity.Schema, entity.TableName)).Append(" (");
        for (int i = 0; i < insertColumns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(QuoteIdentifier(insertColumns[i].ColumnName));
        }
        sb.Append(") VALUES (");
        for (int i = 0; i < insertColumns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(RenderParameter(i));
        }
        sb.Append(") ON CONFLICT (");
        for (int i = 0; i < entity.KeyColumns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(QuoteIdentifier(entity.KeyColumns[i].ColumnName));
        }
        sb.Append(") DO UPDATE SET ");

        bool first = true;
        foreach (var col in insertColumns)
        {
            if (col.IsKey) continue; // don't overwrite the conflict key
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(QuoteIdentifier(col.ColumnName))
              .Append(" = EXCLUDED.")
              .Append(QuoteIdentifier(col.ColumnName));
        }

        return sb.ToString();
    }
}
