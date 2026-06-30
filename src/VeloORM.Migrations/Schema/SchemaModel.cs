namespace VeloORM.Migrations.Schema;

/// <summary>A column in the relational schema model (provider-neutral, store types as strings).</summary>
public sealed class SchemaColumn
{
    public required string Name { get; init; }
    public required string StoreType { get; init; }
    public required bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
}

/// <summary>An index (by name + ordered columns + uniqueness).</summary>
public sealed class SchemaIndex
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public bool IsUnique { get; init; }
}

/// <summary>A table: columns, primary key, and secondary indexes.</summary>
public sealed class SchemaTable
{
    public required string Name { get; init; }
    public string? Schema { get; init; }
    public required IReadOnlyList<SchemaColumn> Columns { get; init; }
    public IReadOnlyList<string> PrimaryKey { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SchemaIndex> Indexes { get; init; } = Array.Empty<SchemaIndex>();

    public SchemaColumn? FindColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The whole schema: a set of tables keyed by (schema, name).</summary>
public sealed class SchemaModel
{
    public required IReadOnlyList<SchemaTable> Tables { get; init; }

    public SchemaTable? FindTable(string? schema, string name) =>
        Tables.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Schema ?? "public", schema ?? "public", StringComparison.OrdinalIgnoreCase));
}
