using System.Text;
using VeloORM.Metadata;
using VeloORM.Sql;

namespace VeloORM.Tests.Unit;

/// <summary>
/// A minimal PostgreSQL-like dialect used to unit-test the dialect-agnostic <c>SqlBuilder</c>
/// without depending on the real provider (which arrives in Phase 2). Identifiers are
/// double-quoted; parameters render as <c>$N</c> (1-based); paging uses LIMIT/OFFSET.
/// </summary>
internal sealed class TestDialect : ISqlDialect
{
    public string Name => "test";

    public string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string QuoteQualifiedName(string? schema, string name) =>
        schema is null ? QuoteIdentifier(name) : QuoteIdentifier(schema) + "." + QuoteIdentifier(name);

    public string RenderParameter(int ordinalZeroBased) => "$" + (ordinalZeroBased + 1).ToString();

    public void AppendPaging(StringBuilder sql, int? limit, int? offset)
    {
        if (limit is { } l) sql.Append(" LIMIT ").Append(l);
        if (offset is { } o) sql.Append(" OFFSET ").Append(o);
    }

    public bool SupportsReturning => true;

    public string GetStoreType(ColumnModel column) => column.StoreType ?? "text";

    public string BuildUpsert(EntityModel entity, IReadOnlyList<ColumnModel> insertColumns) =>
        throw new NotImplementedException();
}
