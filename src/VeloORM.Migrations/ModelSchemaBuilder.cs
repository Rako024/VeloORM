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

            tables.Add(new SchemaTable
            {
                Name = entity.TableName,
                Schema = entity.Schema,
                Columns = columns,
                PrimaryKey = entity.KeyColumns.Select(c => c.ColumnName).ToList(),
                Indexes = indexes,
            });
        }

        return new SchemaModel { Tables = tables };
    }
}
