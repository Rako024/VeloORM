namespace VeloORM.Query;

/// <summary>The terminal operator that determines how results are consumed/shaped.</summary>
public enum QueryTerminal
{
    /// <summary>Materialize all rows (default).</summary>
    List,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    /// <summary><c>SELECT EXISTS(...)</c> / count &gt; 0.</summary>
    Any,
    /// <summary><c>SELECT count(*)</c>.</summary>
    Count,
    /// <summary>A scalar projection materialized per row (<c>Select(x =&gt; x.Prop)</c>).</summary>
    Scalar,
    /// <summary><c>SELECT sum(expr)</c> — a single scalar value.</summary>
    Sum,
    /// <summary><c>SELECT avg(expr)</c> — a single scalar value.</summary>
    Average,
    /// <summary><c>SELECT min(expr)</c> — a single scalar value.</summary>
    Min,
    /// <summary><c>SELECT max(expr)</c> — a single scalar value.</summary>
    Max,
}

public enum JoinKind { Inner, Left }

public sealed class JoinClause(JoinKind kind, string? schema, string table, string alias, SqlExpression on)
{
    public JoinKind Kind { get; } = kind;
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public string Alias { get; } = alias;
    public SqlExpression On { get; } = on;
}

/// <summary>One projected output column: an expression and the result alias used for materialization.</summary>
public sealed class SelectItem(SqlExpression expression, string alias)
{
    public SqlExpression Expression { get; } = expression;
    public string Alias { get; } = alias;
}

public sealed class Ordering(SqlExpression expression, bool descending)
{
    public SqlExpression Expression { get; } = expression;
    public bool Descending { get; } = descending;
}

/// <summary>
/// A relational query against a single root table plus optional joins. Produced from a LINQ
/// expression tree (runtime) or generated at compile time, then rendered by <see cref="SqlBuilder"/>.
/// Mutable during construction; treat as immutable once handed to the builder.
/// </summary>
public sealed class QueryModel(string? schema, string table, string alias)
{
    public string? Schema { get; } = schema;
    public string Table { get; } = table;
    public string Alias { get; } = alias;

    public List<JoinClause> Joins { get; } = new();
    public List<SelectItem> Select { get; } = new();
    public SqlExpression? Where { get; set; }
    public List<SqlExpression> GroupBy { get; } = new();
    public SqlExpression? Having { get; set; }
    public List<Ordering> OrderBy { get; } = new();
    public bool Distinct { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public QueryTerminal Terminal { get; set; } = QueryTerminal.List;
}
