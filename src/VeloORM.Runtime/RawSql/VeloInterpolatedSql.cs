using System.Runtime.CompilerServices;
using System.Text;
using VeloORM.Query;
using VeloORM.Sql;

namespace VeloORM.Runtime;

/// <summary>
/// An interpolated-string handler that builds a parameterized <see cref="SqlStatement"/>: literal text
/// is concatenated as SQL, while every interpolation hole becomes a bound parameter. This makes the
/// raw-SQL escape hatch structurally injection-safe — there is no way to splice a value into the SQL
/// text, only into the parameter list.
/// </summary>
/// <remarks>
/// Deliberately a (non-ref) struct so it can be passed to async methods. It is consumed immediately
/// (converted to a <see cref="SqlStatement"/>) and never stored.
/// </remarks>
[InterpolatedStringHandler]
public struct VeloInterpolatedSql
{
    private readonly ISqlDialect _dialect;
    private readonly StringBuilder _sql;
    private readonly List<SqlParameterBinding> _parameters;

    public VeloInterpolatedSql(int literalLength, int formattedCount, VeloDbContext context)
    {
        _dialect = context.Dialect;
        _sql = new StringBuilder(literalLength + formattedCount * 4);
        _parameters = new List<SqlParameterBinding>(formattedCount);
    }

    /// <summary>Overload so the same injection-safe handler works for raw SQL run on a transaction
    /// (<c>tx.Execute($"...")</c>).</summary>
    public VeloInterpolatedSql(int literalLength, int formattedCount, VeloTransaction transaction)
        : this(literalLength, formattedCount, transaction.Context) { }

    /// <summary>Literal SQL fragments are appended verbatim.</summary>
    public void AppendLiteral(string value) => _sql.Append(value);

    /// <summary>Interpolated values are bound as parameters and rendered as placeholders.</summary>
    public void AppendFormatted<T>(T value)
    {
        _parameters.Add(new SqlParameterBinding(value, typeof(T)));
        _sql.Append(_dialect.RenderParameter(_parameters.Count - 1));
    }

    /// <summary>Ignores any format/alignment specifier — the value is still bound, never inlined.</summary>
    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value);

    internal SqlStatement ToStatement() => new(_sql.ToString(), _parameters.ToArray());
}
