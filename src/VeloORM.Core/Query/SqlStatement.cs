namespace VeloORM.Query;

/// <summary>A bound parameter value plus its CLR type (needed when the value is null).</summary>
public readonly struct SqlParameterBinding(object? value, Type clrType)
{
    public object? Value { get; } = value;
    public Type ClrType { get; } = clrType;
}

/// <summary>The product of rendering a <see cref="QueryModel"/>: SQL text and the ordered
/// list of bound parameters (parameter for <c>$1</c> is <see cref="Parameters"/>[0]).</summary>
public sealed class SqlStatement(string sql, IReadOnlyList<SqlParameterBinding> parameters)
{
    public string Sql { get; } = sql;
    public IReadOnlyList<SqlParameterBinding> Parameters { get; } = parameters;
}
