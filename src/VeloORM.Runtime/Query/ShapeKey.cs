using System.Text;
using VeloORM.Query;

namespace VeloORM.Runtime.Query;

/// <summary>
/// Computes a cache key from a query's <em>structure</em>, deliberately excluding parameter values
/// (a parameter contributes only its CLR type). Two queries that differ only in bound values produce
/// the same key and therefore reuse the same compiled SQL + materializer. Cheap to compute and
/// independent of the dialect, so it can be derived per-call without rendering SQL.
/// </summary>
internal static class ShapeKey
{
    public static string Compute(QueryModel q, ProjectionPlan projection)
    {
        var sb = new StringBuilder(128);
        sb.Append("T:").Append((int)q.Terminal);
        sb.Append("|D:").Append(q.Distinct ? '1' : '0');
        sb.Append("|F:").Append(q.Schema).Append('.').Append(q.Table).Append(' ').Append(q.Alias);

        foreach (var join in q.Joins)
        {
            sb.Append("|J:").Append((int)join.Kind).Append(join.Schema).Append('.').Append(join.Table).Append(' ').Append(join.Alias).Append(" ON ");
            Append(sb, join.On);
        }

        sb.Append("|S:");
        foreach (var item in q.Select)
        {
            sb.Append(item.Alias).Append('=');
            Append(sb, item.Expression);
            sb.Append(',');
        }

        if (q.Where is not null) { sb.Append("|W:"); Append(sb, q.Where); }

        if (q.GroupBy.Count > 0)
        {
            sb.Append("|G:");
            foreach (var g in q.GroupBy) { Append(sb, g); sb.Append(','); }
        }

        if (q.Having is not null) { sb.Append("|H:"); Append(sb, q.Having); }

        if (q.OrderBy.Count > 0)
        {
            sb.Append("|O:");
            foreach (var o in q.OrderBy) { Append(sb, o.Expression); sb.Append(o.Descending ? "d" : "a").Append(','); }
        }

        if (q.Limit is { } l) sb.Append("|L:").Append(l);
        if (q.Offset is { } o2) sb.Append("|X:").Append(o2);

        sb.Append("|P:").Append((int)projection.Kind).Append(':').Append(projection.ResultType.FullName);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, SqlExpression expression)
    {
        switch (expression)
        {
            case SqlColumn c:
                sb.Append('"').Append(c.TableAlias).Append('.').Append(c.ColumnName).Append('"');
                break;
            case SqlParameter p:
                sb.Append("?:").Append(p.ClrType.Name); // type only — never the value
                break;
            case SqlLiteral lit:
                sb.Append(lit.Text);
                break;
            case SqlBinary b:
                sb.Append('('); Append(sb, b.Left); sb.Append(' ').Append((int)b.Operator).Append(' '); Append(sb, b.Right); sb.Append(')');
                break;
            case SqlUnary u:
                sb.Append((int)u.Operator).Append('('); Append(sb, u.Operand); sb.Append(')');
                break;
            case SqlIsNull n:
                Append(sb, n.Operand); sb.Append(n.Negated ? " notnull" : " isnull");
                break;
            case SqlLike like:
                Append(sb, like.Operand); sb.Append(" like "); Append(sb, like.Pattern);
                break;
            case SqlIn inExpr:
                Append(sb, inExpr.Operand); sb.Append(" in[").Append(inExpr.Values.Count).Append(']');
                break;
            case SqlFunction fn:
                sb.Append(fn.Name).Append('(');
                foreach (var a in fn.Arguments) { Append(sb, a); sb.Append(','); }
                sb.Append(')');
                break;
            default:
                sb.Append('?').Append(expression.GetType().Name);
                break;
        }
    }
}
