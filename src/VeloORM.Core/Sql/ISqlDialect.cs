using System.Text;
using VeloORM.Metadata;

namespace VeloORM.Sql;

/// <summary>
/// Abstracts every database-specific concern so the query model and engines stay
/// dialect-agnostic. Only PostgreSQL is fully implemented in v1, but no provider-specific
/// behavior may leak outside an <see cref="ISqlDialect"/> implementation.
/// </summary>
public interface ISqlDialect
{
    /// <summary>Short dialect identifier, e.g. <c>"postgres"</c>. Used in cache keys.</summary>
    string Name { get; }

    /// <summary>Quotes a single identifier (table/column), escaping the quote char.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>Quotes a possibly-schema-qualified table name.</summary>
    string QuoteQualifiedName(string? schema, string name);

    /// <summary>Renders the placeholder for a bound parameter by zero-based ordinal
    /// (e.g. PostgreSQL renders ordinal 0 as <c>$1</c>).</summary>
    string RenderParameter(int ordinalZeroBased);

    /// <summary>Appends the dialect's paging clause (LIMIT/OFFSET equivalent). No-op when both null.</summary>
    void AppendPaging(StringBuilder sql, int? limit, int? offset);

    /// <summary>True when the dialect supports an <c>INSERT ... RETURNING</c>-style clause.</summary>
    bool SupportsReturning { get; }

    /// <summary>The DDL store type for a column (e.g. <c>integer</c>, <c>text</c>), honoring any
    /// explicit <see cref="ColumnModel.StoreType"/>. Used for code-first DDL (Phase 8).</summary>
    string GetStoreType(ColumnModel column);

    /// <summary>Builds an upsert (INSERT ... ON CONFLICT / MERGE) statement for the entity.
    /// Implemented per dialect; used by the writer in later phases.</summary>
    string BuildUpsert(EntityModel entity, IReadOnlyList<ColumnModel> insertColumns);
}
