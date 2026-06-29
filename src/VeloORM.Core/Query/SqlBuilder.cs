using System.Text;
using VeloORM.Sql;

namespace VeloORM.Query;

/// <summary>
/// Renders a <see cref="QueryModel"/> into a parameterized <see cref="SqlStatement"/> using an
/// <see cref="ISqlDialect"/>. This is the single SQL-generation path shared by the runtime engine
/// and the source generator. Values only ever appear as bound parameters.
/// </summary>
public sealed class SqlBuilder
{
    private readonly ISqlDialect _dialect;
    private readonly StringBuilder _sql = new(256);
    private readonly List<SqlParameterBinding> _parameters = new();

    public SqlBuilder(ISqlDialect dialect) => _dialect = dialect;

    /// <summary>Convenience: render a query model in one call.</summary>
    public static SqlStatement Build(QueryModel query, ISqlDialect dialect) =>
        new SqlBuilder(dialect).BuildStatement(query);

    public SqlStatement BuildStatement(QueryModel query)
    {
        _sql.Clear();
        _parameters.Clear();

        switch (query.Terminal)
        {
            case QueryTerminal.Count:
                BuildCount(query);
                break;
            case QueryTerminal.Any:
                BuildAny(query);
                break;
            default:
                BuildSelect(query);
                break;
        }

        return new SqlStatement(_sql.ToString(), _parameters.ToArray());
    }

    private void BuildSelect(QueryModel q)
    {
        _sql.Append("SELECT ");
        if (q.Distinct)
            _sql.Append("DISTINCT ");

        if (q.Select.Count == 0)
            throw new InvalidOperationException("Query projection is empty; at least one select item is required.");

        for (int i = 0; i < q.Select.Count; i++)
        {
            if (i > 0) _sql.Append(", ");
            var item = q.Select[i];
            AppendExpression(item.Expression);
            _sql.Append(" AS ").Append(_dialect.QuoteIdentifier(item.Alias));
        }

        AppendFromAndJoins(q);
        AppendWhere(q.Where);
        AppendGroupBy(q);
        AppendHaving(q.Having);
        AppendOrderBy(q);
        AppendPaging(q);
    }

    private void BuildCount(QueryModel q)
    {
        _sql.Append("SELECT count(*)");
        AppendFromAndJoins(q);
        AppendWhere(q.Where);
        AppendGroupBy(q);
        AppendHaving(q.Having);
    }

    private void BuildAny(QueryModel q)
    {
        _sql.Append("SELECT EXISTS(SELECT 1");
        AppendFromAndJoins(q);
        AppendWhere(q.Where);
        _sql.Append(')');
    }

    private void AppendFromAndJoins(QueryModel q)
    {
        _sql.Append(" FROM ")
            .Append(_dialect.QuoteQualifiedName(q.Schema, q.Table))
            .Append(" AS ")
            .Append(_dialect.QuoteIdentifier(q.Alias));

        foreach (var join in q.Joins)
        {
            _sql.Append(join.Kind == JoinKind.Left ? " LEFT JOIN " : " INNER JOIN ")
                .Append(_dialect.QuoteQualifiedName(join.Schema, join.Table))
                .Append(" AS ")
                .Append(_dialect.QuoteIdentifier(join.Alias))
                .Append(" ON ");
            AppendExpression(join.On);
        }
    }

    private void AppendWhere(SqlExpression? where)
    {
        if (where is null) return;
        _sql.Append(" WHERE ");
        AppendExpression(where);
    }

    private void AppendGroupBy(QueryModel q)
    {
        if (q.GroupBy.Count == 0) return;
        _sql.Append(" GROUP BY ");
        for (int i = 0; i < q.GroupBy.Count; i++)
        {
            if (i > 0) _sql.Append(", ");
            AppendExpression(q.GroupBy[i]);
        }
    }

    private void AppendHaving(SqlExpression? having)
    {
        if (having is null) return;
        _sql.Append(" HAVING ");
        AppendExpression(having);
    }

    private void AppendOrderBy(QueryModel q)
    {
        if (q.OrderBy.Count == 0) return;
        _sql.Append(" ORDER BY ");
        for (int i = 0; i < q.OrderBy.Count; i++)
        {
            if (i > 0) _sql.Append(", ");
            AppendExpression(q.OrderBy[i].Expression);
            if (q.OrderBy[i].Descending) _sql.Append(" DESC");
        }
    }

    private void AppendPaging(QueryModel q)
    {
        // Terminal operators imply row limits where the caller did not set one explicitly.
        int? limit = q.Limit;
        limit ??= q.Terminal switch
        {
            QueryTerminal.First or QueryTerminal.FirstOrDefault => 1,
            QueryTerminal.Single or QueryTerminal.SingleOrDefault => 2, // fetch 2 to detect non-uniqueness
            _ => null,
        };
        _dialect.AppendPaging(_sql, limit, q.Offset);
    }

    private void AppendExpression(SqlExpression expression)
    {
        switch (expression)
        {
            case SqlColumn col:
                if (col.TableAlias is not null)
                    _sql.Append(_dialect.QuoteIdentifier(col.TableAlias)).Append('.');
                _sql.Append(_dialect.QuoteIdentifier(col.ColumnName));
                break;

            case SqlParameter p:
                _parameters.Add(new SqlParameterBinding(p.Value, p.ClrType));
                _sql.Append(_dialect.RenderParameter(_parameters.Count - 1));
                break;

            case SqlLiteral lit:
                _sql.Append(lit.Text);
                break;

            case SqlBinary b:
                _sql.Append('(');
                AppendExpression(b.Left);
                _sql.Append(' ').Append(RenderBinaryOperator(b.Operator)).Append(' ');
                AppendExpression(b.Right);
                _sql.Append(')');
                break;

            case SqlUnary u:
                if (u.Operator == SqlUnaryOperator.Not)
                {
                    _sql.Append("(NOT ");
                    AppendExpression(u.Operand);
                    _sql.Append(')');
                }
                else // Negate
                {
                    _sql.Append("(-");
                    AppendExpression(u.Operand);
                    _sql.Append(')');
                }
                break;

            case SqlIsNull isNull:
                _sql.Append('(');
                AppendExpression(isNull.Operand);
                _sql.Append(isNull.Negated ? " IS NOT NULL" : " IS NULL");
                _sql.Append(')');
                break;

            case SqlLike like:
                _sql.Append('(');
                AppendExpression(like.Operand);
                _sql.Append(" LIKE ");
                AppendExpression(like.Pattern);
                _sql.Append(')');
                break;

            case SqlIn inExpr:
                AppendExpression(inExpr.Operand);
                _sql.Append(" IN (");
                for (int i = 0; i < inExpr.Values.Count; i++)
                {
                    if (i > 0) _sql.Append(", ");
                    AppendExpression(inExpr.Values[i]);
                }
                _sql.Append(')');
                break;

            case SqlFunction fn:
                _sql.Append(fn.Name).Append('(');
                if (fn.Arguments.Count == 0 && fn.IsAggregate)
                {
                    _sql.Append('*');
                }
                else
                {
                    for (int i = 0; i < fn.Arguments.Count; i++)
                    {
                        if (i > 0) _sql.Append(", ");
                        AppendExpression(fn.Arguments[i]);
                    }
                }
                _sql.Append(')');
                break;

            default:
                throw new NotSupportedException($"SQL expression node '{expression.GetType().Name}' is not supported.");
        }
    }

    private static string RenderBinaryOperator(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.And => "AND",
        SqlBinaryOperator.Or => "OR",
        SqlBinaryOperator.Equal => "=",
        SqlBinaryOperator.NotEqual => "<>",
        SqlBinaryOperator.GreaterThan => ">",
        SqlBinaryOperator.GreaterThanOrEqual => ">=",
        SqlBinaryOperator.LessThan => "<",
        SqlBinaryOperator.LessThanOrEqual => "<=",
        SqlBinaryOperator.Add => "+",
        SqlBinaryOperator.Subtract => "-",
        SqlBinaryOperator.Multiply => "*",
        SqlBinaryOperator.Divide => "/",
        SqlBinaryOperator.Modulo => "%",
        SqlBinaryOperator.Concat => "||",
        _ => throw new NotSupportedException($"Binary operator '{op}' is not supported."),
    };
}
