using VeloORM.Metadata;
using VeloORM.Migrations;
using VeloORM.Migrations.Schema;

namespace VeloORM.Tests.Unit;

public class ForeignKeyMigrationTests
{
    private static readonly TestDialect Dialect = new();

    [Fact]
    public void ModelSchemaBuilder_Emits_ForeignKey_From_Reference_Navigation()
    {
        var model = VeloModel.Build([typeof(NavUser), typeof(NavOrder)]);
        var schema = ModelSchemaBuilder.Build(model, Dialect);

        var orders = schema.FindTable(null, model.GetEntity<NavOrder>().TableName);
        Assert.NotNull(orders);
        var fk = Assert.Single(orders!.ForeignKeys);
        Assert.Equal(new[] { "user_id" }, fk.Columns.ToArray());
        Assert.Equal(model.GetEntity<NavUser>().TableName, fk.PrincipalTable);
        Assert.Equal(new[] { "id" }, fk.PrincipalColumns.ToArray());

        // The principal table (users) holds no FK of its own (its Orders nav is a collection).
        var users = schema.FindTable(null, model.GetEntity<NavUser>().TableName)!;
        Assert.Empty(users.ForeignKeys);
    }

    [Fact]
    public void Differ_Produces_AddForeignKey_After_Create_Tables()
    {
        var model = VeloModel.Build([typeof(NavUser), typeof(NavOrder)]);
        var desired = ModelSchemaBuilder.Build(model, Dialect);
        var empty = new SchemaModel { Tables = Array.Empty<SchemaTable>() };

        var ops = SchemaDiffer.Diff(empty, desired);

        var addFk = ops.OfType<AddForeignKeyOperation>().Single();
        var lastCreate = ops.FindLastIndex(o => o is CreateTableOperation);
        Assert.True(ops.IndexOf(addFk) > lastCreate, "FK must be added after all tables exist.");
    }

    [Fact]
    public void Differ_Produces_DropForeignKey_When_Removed()
    {
        var fk = new SchemaForeignKey
        {
            Name = "fk_orders_user_id",
            Columns = new[] { "user_id" },
            PrincipalTable = "users",
            PrincipalColumns = new[] { "id" },
        };
        var col = new SchemaColumn { Name = "user_id", StoreType = "integer", IsNullable = false };
        var withFk = Table("orders", col, new[] { fk });
        var withoutFk = Table("orders", col, Array.Empty<SchemaForeignKey>());

        var dropOps = SchemaDiffer.Diff(Schema(withFk), Schema(withoutFk));
        Assert.Contains(dropOps, o => o is DropForeignKeyOperation d && d.Name == "fk_orders_user_id");

        var addOps = SchemaDiffer.Diff(Schema(withoutFk), Schema(withFk));
        Assert.Contains(addOps, o => o is AddForeignKeyOperation a && a.ForeignKey.Name == "fk_orders_user_id");
    }

    [Fact]
    public void SqlGenerator_Renders_Add_And_Drop_Constraint_Ddl()
    {
        var gen = new PostgresMigrationSqlGenerator(Dialect);
        var fk = new SchemaForeignKey
        {
            Name = "fk_orders_user_id",
            Columns = new[] { "user_id" },
            PrincipalTable = "users",
            PrincipalColumns = new[] { "id" },
        };

        var add = gen.Generate([new AddForeignKeyOperation(null, "orders", fk)]);
        Assert.Contains("ALTER TABLE \"orders\" ADD CONSTRAINT \"fk_orders_user_id\"", add);
        Assert.Contains("FOREIGN KEY (\"user_id\")", add);
        Assert.Contains("REFERENCES \"users\" (\"id\")", add);

        var drop = gen.Generate([new DropForeignKeyOperation(null, "orders", "fk_orders_user_id")]);
        Assert.Contains("ALTER TABLE \"orders\" DROP CONSTRAINT \"fk_orders_user_id\"", drop);
    }

    private static SchemaTable Table(string name, SchemaColumn column, IReadOnlyList<SchemaForeignKey> fks) =>
        new() { Name = name, Columns = new[] { column }, ForeignKeys = fks };

    private static SchemaModel Schema(params SchemaTable[] tables) => new() { Tables = tables };
}
