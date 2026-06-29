namespace VeloORM.Query;

/// <summary>Base type for every node in the internal SQL expression tree (the query AST).
/// This single model is translated to SQL by both the runtime engine and the source generator.</summary>
public abstract class SqlExpression;

public enum SqlBinaryOperator
{
    And, Or,
    Equal, NotEqual,
    GreaterThan, GreaterThanOrEqual,
    LessThan, LessThanOrEqual,
    Add, Subtract, Multiply, Divide, Modulo,
    Concat,
}

public enum SqlUnaryOperator { Not, Negate }

/// <summary>A reference to a column, optionally qualified by a table alias.</summary>
public sealed class SqlColumn(string? tableAlias, string columnName, Type clrType) : SqlExpression
{
    public string? TableAlias { get; } = tableAlias;
    public string ColumnName { get; } = columnName;
    public Type ClrType { get; } = clrType;
}

/// <summary>A bound value. NEVER inlined — always rendered as a dialect placeholder and bound.
/// This is the structural guarantee against SQL injection.</summary>
public sealed class SqlParameter(object? value, Type clrType) : SqlExpression
{
    public object? Value { get; } = value;
    public Type ClrType { get; } = clrType;

    /// <summary>
    /// Pre-assigned zero-based placeholder ordinal. When &gt;= 0 the builder renders this exact
    /// ordinal (and does not collect the value itself); the caller supplies the binding list in
    /// ordinal order. When -1 (the default) the builder self-assigns ordinals in render order and
    /// collects the bindings. The runtime engine uses the former so cache hits skip SQL rendering.
    /// </summary>
    public int Ordinal { get; set; } = -1;
}

/// <summary>A constant rendered literally into SQL. Restricted to dialect-safe tokens
/// (e.g. <c>NULL</c>, <c>*</c>); values must use <see cref="SqlParameter"/> instead.</summary>
public sealed class SqlLiteral(string text) : SqlExpression
{
    public string Text { get; } = text;
    public static readonly SqlLiteral Null = new("NULL");
    public static readonly SqlLiteral Star = new("*");
}

public sealed class SqlBinary(SqlExpression left, SqlBinaryOperator op, SqlExpression right) : SqlExpression
{
    public SqlExpression Left { get; } = left;
    public SqlBinaryOperator Operator { get; } = op;
    public SqlExpression Right { get; } = right;
}

public sealed class SqlUnary(SqlUnaryOperator op, SqlExpression operand) : SqlExpression
{
    public SqlUnaryOperator Operator { get; } = op;
    public SqlExpression Operand { get; } = operand;
}

/// <summary>A SQL function call, e.g. <c>upper(x)</c>, <c>count(*)</c> (empty args = star).</summary>
public sealed class SqlFunction(string name, IReadOnlyList<SqlExpression> arguments, bool isAggregate = false) : SqlExpression
{
    public string Name { get; } = name;
    public IReadOnlyList<SqlExpression> Arguments { get; } = arguments;
    public bool IsAggregate { get; } = isAggregate;

    public static SqlFunction CountStar() => new("count", Array.Empty<SqlExpression>(), isAggregate: true);
}

/// <summary><c>operand IS [NOT] NULL</c>.</summary>
public sealed class SqlIsNull(SqlExpression operand, bool negated) : SqlExpression
{
    public SqlExpression Operand { get; } = operand;
    public bool Negated { get; } = negated;
}

/// <summary><c>operand LIKE pattern</c>. The pattern is a bound parameter.</summary>
public sealed class SqlLike(SqlExpression operand, SqlExpression pattern) : SqlExpression
{
    public SqlExpression Operand { get; } = operand;
    public SqlExpression Pattern { get; } = pattern;
}

/// <summary><c>operand IN (v1, v2, ...)</c>. Each value is a bound parameter.</summary>
public sealed class SqlIn(SqlExpression operand, IReadOnlyList<SqlExpression> values) : SqlExpression
{
    public SqlExpression Operand { get; } = operand;
    public IReadOnlyList<SqlExpression> Values { get; } = values;
}
