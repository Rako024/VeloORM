using VeloORM.Metadata;
using VeloORM.Query;

namespace VeloORM.Tests.Unit;

public class SqlBuilderTests
{
    private static readonly TestDialect Dialect = new();
    private static readonly EntityModel UserModel = VeloModel.Build([typeof(User)]).GetEntity<User>();

    private static QueryModel SelectAll(string alias = "u")
    {
        var q = new QueryModel(UserModel.Schema, UserModel.TableName, alias);
        foreach (var col in UserModel.Columns)
            q.Select.Add(new SelectItem(new SqlColumn(alias, col.ColumnName, col.ClrType), col.ColumnName));
        return q;
    }

    private static SqlColumn Col(string columnName, Type type, string alias = "u") => new(alias, columnName, type);

    [Fact]
    public void Select_With_Where_Renders_Qualified_Columns_And_Bound_Parameter()
    {
        var q = SelectAll();
        q.Where = new SqlBinary(Col("login_count", typeof(int)), SqlBinaryOperator.GreaterThan,
            new SqlParameter(5, typeof(int)));

        var stmt = SqlBuilder.Build(q, Dialect);

        Assert.StartsWith("SELECT \"u\".\"id\" AS \"id\"", stmt.Sql);
        Assert.Contains("FROM \"users\" AS \"u\"", stmt.Sql);
        Assert.Contains("WHERE (\"u\".\"login_count\" > $1)", stmt.Sql);
    }

    [Fact]
    public void Values_Are_Bound_Never_Inlined()
    {
        var q = SelectAll();
        q.Where = new SqlBinary(Col("name", typeof(string)), SqlBinaryOperator.Equal,
            new SqlParameter("Robert'); DROP TABLE users;--", typeof(string)));

        var stmt = SqlBuilder.Build(q, Dialect);

        // The malicious value must NOT appear in the SQL text; it is a bound parameter.
        Assert.DoesNotContain("DROP TABLE", stmt.Sql);
        Assert.Contains("= $1", stmt.Sql);
        Assert.Single(stmt.Parameters);
        Assert.Equal("Robert'); DROP TABLE users;--", stmt.Parameters[0].Value);
    }

    [Fact]
    public void Multiple_Parameters_Are_Numbered_In_Order()
    {
        var q = SelectAll();
        q.Where = new SqlBinary(
            new SqlBinary(Col("login_count", typeof(int)), SqlBinaryOperator.GreaterThanOrEqual, new SqlParameter(1, typeof(int))),
            SqlBinaryOperator.And,
            new SqlBinary(Col("name", typeof(string)), SqlBinaryOperator.Equal, new SqlParameter("ann", typeof(string))));

        var stmt = SqlBuilder.Build(q, Dialect);

        Assert.Contains("$1", stmt.Sql);
        Assert.Contains("$2", stmt.Sql);
        Assert.Equal(2, stmt.Parameters.Count);
        Assert.Equal(1, stmt.Parameters[0].Value);
        Assert.Equal("ann", stmt.Parameters[1].Value);
    }

    [Fact]
    public void OrderBy_And_Paging_Are_Rendered()
    {
        var q = SelectAll();
        q.OrderBy.Add(new Ordering(Col("created_at", typeof(DateTimeOffset)), descending: true));
        q.Limit = 10;
        q.Offset = 20;

        var stmt = SqlBuilder.Build(q, Dialect);

        Assert.Contains("ORDER BY \"u\".\"created_at\" DESC", stmt.Sql);
        Assert.Contains("LIMIT 10 OFFSET 20", stmt.Sql);
    }

    [Fact]
    public void First_Implies_Limit_1()
    {
        var q = SelectAll();
        q.Terminal = QueryTerminal.First;
        var stmt = SqlBuilder.Build(q, Dialect);
        Assert.Contains("LIMIT 1", stmt.Sql);
    }

    [Fact]
    public void Single_Fetches_Two_Rows_To_Detect_NonUniqueness()
    {
        var q = SelectAll();
        q.Terminal = QueryTerminal.SingleOrDefault;
        var stmt = SqlBuilder.Build(q, Dialect);
        Assert.Contains("LIMIT 2", stmt.Sql);
    }

    [Fact]
    public void Count_Terminal_Renders_Count_Star()
    {
        var q = SelectAll();
        q.Terminal = QueryTerminal.Count;
        var stmt = SqlBuilder.Build(q, Dialect);
        Assert.StartsWith("SELECT count(*) FROM \"users\" AS \"u\"", stmt.Sql);
    }

    [Fact]
    public void Any_Terminal_Renders_Exists()
    {
        var q = SelectAll();
        q.Where = new SqlBinary(Col("login_count", typeof(int)), SqlBinaryOperator.GreaterThan, new SqlParameter(0, typeof(int)));
        q.Terminal = QueryTerminal.Any;
        var stmt = SqlBuilder.Build(q, Dialect);
        Assert.StartsWith("SELECT EXISTS(SELECT 1 FROM \"users\" AS \"u\" WHERE", stmt.Sql);
        Assert.EndsWith(")", stmt.Sql);
    }

    [Fact]
    public void Sum_Terminal_Renders_Aggregate_Without_OrderBy_Or_Paging()
    {
        var q = new QueryModel(UserModel.Schema, UserModel.TableName, "u");
        q.Select.Add(new SelectItem(
            new SqlFunction("sum", new SqlExpression[] { Col("login_count", typeof(int)) }, isAggregate: true), "c0"));
        q.Where = new SqlBinary(Col("login_count", typeof(int)), SqlBinaryOperator.GreaterThan, new SqlParameter(0, typeof(int)));
        q.OrderBy.Add(new Ordering(Col("login_count", typeof(int)), descending: true)); // must be ignored
        q.Limit = 5; // must be ignored
        q.Terminal = QueryTerminal.Sum;

        var stmt = SqlBuilder.Build(q, Dialect);

        Assert.StartsWith("SELECT sum(\"u\".\"login_count\") FROM \"users\" AS \"u\"", stmt.Sql);
        Assert.Contains("WHERE (\"u\".\"login_count\" > $1)", stmt.Sql);
        Assert.DoesNotContain("ORDER BY", stmt.Sql);
        Assert.DoesNotContain("LIMIT", stmt.Sql);
    }

    [Theory]
    [InlineData(QueryTerminal.Min, "min")]
    [InlineData(QueryTerminal.Max, "max")]
    [InlineData(QueryTerminal.Average, "avg")]
    public void Aggregate_Terminals_Render_Their_Function(QueryTerminal terminal, string fn)
    {
        var q = new QueryModel(UserModel.Schema, UserModel.TableName, "u");
        q.Select.Add(new SelectItem(
            new SqlFunction(fn, new SqlExpression[] { Col("login_count", typeof(int)) }, isAggregate: true), "c0"));
        q.Terminal = terminal;

        var stmt = SqlBuilder.Build(q, Dialect);

        Assert.Equal($"SELECT {fn}(\"u\".\"login_count\") FROM \"users\" AS \"u\"", stmt.Sql);
    }

    [Fact]
    public void IsNull_Like_And_In_Render_Correctly()
    {
        var q = SelectAll();
        q.Where = new SqlBinary(
            new SqlIsNull(Col("email", typeof(string)), negated: true),
            SqlBinaryOperator.And,
            new SqlIn(Col("login_count", typeof(int)),
                [new SqlParameter(1, typeof(int)), new SqlParameter(2, typeof(int))]));

        var stmt = SqlBuilder.Build(q, Dialect);

        Assert.Contains("\"u\".\"email\" IS NOT NULL", stmt.Sql);
        Assert.Contains("\"u\".\"login_count\" IN ($1, $2)", stmt.Sql);
    }
}
