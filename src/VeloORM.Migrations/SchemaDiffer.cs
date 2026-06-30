using VeloORM.Migrations.Schema;

namespace VeloORM.Migrations;

/// <summary>Computes the operations required to transform schema <c>from</c> into schema <c>to</c>.</summary>
public static class SchemaDiffer
{
    public static List<MigrationOperation> Diff(SchemaModel from, SchemaModel to)
    {
        var operations = new List<MigrationOperation>();
        // Foreign keys are appended last so every referenced table already exists.
        var foreignKeyOps = new List<MigrationOperation>();

        // Dropped tables: drop their foreign keys first so no dependency blocks the table drops
        // (the drop order across tables is otherwise unconstrained).
        var droppedTables = from.Tables.Where(t => to.FindTable(t.Schema, t.Name) is null).ToList();
        foreach (var oldTable in droppedTables)
            foreach (var fk in oldTable.ForeignKeys)
                operations.Add(new DropForeignKeyOperation(oldTable.Schema, oldTable.Name, fk.Name));
        foreach (var oldTable in droppedTables)
            operations.Add(new DropTableOperation(oldTable.Schema, oldTable.Name));

        foreach (var newTable in to.Tables)
        {
            var oldTable = from.FindTable(newTable.Schema, newTable.Name);
            if (oldTable is null)
            {
                operations.Add(new CreateTableOperation(newTable));
                foreach (var index in newTable.Indexes)
                    operations.Add(new CreateIndexOperation(newTable.Schema, newTable.Name, index));
                foreach (var fk in newTable.ForeignKeys)
                    foreignKeyOps.Add(new AddForeignKeyOperation(newTable.Schema, newTable.Name, fk));
                continue;
            }

            DiffColumns(oldTable, newTable, operations);
            DiffIndexes(oldTable, newTable, operations);
            DiffForeignKeys(oldTable, newTable, operations, foreignKeyOps);
        }

        operations.AddRange(foreignKeyOps);
        return operations;
    }

    private static void DiffColumns(SchemaTable oldTable, SchemaTable newTable, List<MigrationOperation> ops)
    {
        foreach (var oldCol in oldTable.Columns)
            if (newTable.FindColumn(oldCol.Name) is null)
                ops.Add(new DropColumnOperation(newTable.Schema, newTable.Name, oldCol.Name));

        foreach (var newCol in newTable.Columns)
        {
            var oldCol = oldTable.FindColumn(newCol.Name);
            if (oldCol is null)
                ops.Add(new AddColumnOperation(newTable.Schema, newTable.Name, newCol));
            else if (!TypesEqual(oldCol.StoreType, newCol.StoreType) || oldCol.IsNullable != newCol.IsNullable)
                ops.Add(new AlterColumnOperation(newTable.Schema, newTable.Name, newCol));
        }
    }

    private static void DiffIndexes(SchemaTable oldTable, SchemaTable newTable, List<MigrationOperation> ops)
    {
        foreach (var oldIx in oldTable.Indexes)
            if (!newTable.Indexes.Any(i => NameEq(i.Name, oldIx.Name)))
                ops.Add(new DropIndexOperation(newTable.Schema, oldIx.Name));

        foreach (var newIx in newTable.Indexes)
            if (!oldTable.Indexes.Any(i => NameEq(i.Name, newIx.Name)))
                ops.Add(new CreateIndexOperation(newTable.Schema, newTable.Name, newIx));
    }

    private static void DiffForeignKeys(SchemaTable oldTable, SchemaTable newTable, List<MigrationOperation> ops, List<MigrationOperation> addOps)
    {
        foreach (var oldFk in oldTable.ForeignKeys)
            if (!newTable.ForeignKeys.Any(f => NameEq(f.Name, oldFk.Name)))
                ops.Add(new DropForeignKeyOperation(newTable.Schema, newTable.Name, oldFk.Name));

        foreach (var newFk in newTable.ForeignKeys)
            if (!oldTable.ForeignKeys.Any(f => NameEq(f.Name, newFk.Name)))
                addOps.Add(new AddForeignKeyOperation(newTable.Schema, newTable.Name, newFk));
    }

    private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TypesEqual(string a, string b) => Normalize(a) == Normalize(b);

    private static string Normalize(string storeType)
    {
        var t = storeType.Trim().ToLowerInvariant();
        var paren = t.IndexOf('(');
        return paren >= 0 ? t.Substring(0, paren).Trim() : t;
    }
}
