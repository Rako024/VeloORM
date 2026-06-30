namespace VeloORM.Migrations.Schema;

/// <summary>Base type for a single schema-change operation produced by the differ.</summary>
public abstract class MigrationOperation;

public sealed class CreateTableOperation(SchemaTable table) : MigrationOperation
{
    public SchemaTable Table { get; } = table;
}

public sealed class DropTableOperation(string? schema, string name) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Name { get; } = name;
}

public sealed class AddColumnOperation(string? schema, string table, SchemaColumn column) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public SchemaColumn Column { get; } = column;
}

public sealed class DropColumnOperation(string? schema, string table, string column) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public string Column { get; } = column;
}

/// <summary>Alters a column's store type and/or nullability.</summary>
public sealed class AlterColumnOperation(string? schema, string table, SchemaColumn target) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public SchemaColumn Target { get; } = target;
}

public sealed class CreateIndexOperation(string? schema, string table, SchemaIndex index) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public SchemaIndex Index { get; } = index;
}

public sealed class DropIndexOperation(string? schema, string indexName) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string IndexName { get; } = indexName;
}

/// <summary>Adds a foreign-key constraint via <c>ALTER TABLE … ADD CONSTRAINT</c> (emitted after all
/// tables exist, so the referenced table is guaranteed present).</summary>
public sealed class AddForeignKeyOperation(string? schema, string table, SchemaForeignKey foreignKey) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public SchemaForeignKey ForeignKey { get; } = foreignKey;
}

public sealed class DropForeignKeyOperation(string? schema, string table, string name) : MigrationOperation
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public string Name { get; } = name;
}
