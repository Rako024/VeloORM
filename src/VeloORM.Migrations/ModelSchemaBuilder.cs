using VeloORM.Metadata;
using VeloORM.Migrations.Schema;
using VeloORM.Sql;

namespace VeloORM.Migrations;

/// <summary>Builds the desired <see cref="SchemaModel"/> from a resolved <see cref="VeloModel"/>.</summary>
public static class ModelSchemaBuilder
{
    public static SchemaModel Build(VeloModel model, ISqlDialect dialect)
    {
        var tables = new List<SchemaTable>();

        foreach (var entity in model.Entities)
        {
            var columns = entity.Columns.Select(c => new SchemaColumn
            {
                Name = c.ColumnName,
                StoreType = dialect.GetStoreType(c),
                IsNullable = c.IsNullable,
                IsIdentity = c.IsKey && c.StoreGenerated == StoreGenerated.OnAdd,
            }).ToList();

            var indexes = entity.Indexes.Select(ix => new SchemaIndex
            {
                Name = ix.Name,
                Columns = ix.Columns.Select(c => c.ColumnName).ToList(),
                IsUnique = ix.IsUnique,
            }).ToList();

            // Reference navigations (FK lives on this entity) become foreign-key constraints.
            var foreignKeys = new List<SchemaForeignKey>();
            foreach (var nav in entity.Navigations.Where(n => n.Kind == NavigationKind.Reference))
            {
                // Only emit when the FK column is mapped and the principal table is in the model
                // (correctness: never guess a constraint we can't fully resolve).
                var target = model.FindEntity(nav.TargetClrType);
                if (target is null || !columns.Any(c => c.Name == nav.LocalKeyColumnName))
                    continue;

                foreignKeys.Add(new SchemaForeignKey
                {
                    Name = $"fk_{entity.TableName}_{nav.LocalKeyColumnName}",
                    Columns = new[] { nav.LocalKeyColumnName },
                    PrincipalTable = target.TableName,
                    PrincipalSchema = target.Schema,
                    PrincipalColumns = new[] { nav.TargetKeyColumnName },
                });
            }

            tables.Add(new SchemaTable
            {
                Name = entity.TableName,
                Schema = entity.Schema,
                Columns = columns,
                PrimaryKey = entity.KeyColumns.Select(c => c.ColumnName).ToList(),
                Indexes = indexes,
                ForeignKeys = foreignKeys,
            });
        }

        return new SchemaModel { Tables = tables };
    }
}
