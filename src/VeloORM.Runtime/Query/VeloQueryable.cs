using System.Collections;
using System.Linq.Expressions;

namespace VeloORM.Runtime.Query;

/// <summary>
/// The <c>IQueryable&lt;T&gt;</c> (and <c>IOrderedQueryable&lt;T&gt;</c>) implementation returned by
/// <see cref="VeloDbContext.Set{TEntity}"/>. Carries the LINQ expression that the provider translates
/// to SQL on enumeration / terminal operator.
/// </summary>
internal sealed class VeloQueryable<T> : IOrderedQueryable<T>
{
    private readonly VeloQueryProvider _provider;
    private readonly Expression _expression;

    /// <summary>Root queryable: the expression is a constant referencing this instance.</summary>
    public VeloQueryable(VeloQueryProvider provider)
    {
        _provider = provider;
        _expression = Expression.Constant(this);
    }

    /// <summary>Derived queryable produced by an operator (Where/Select/...).</summary>
    public VeloQueryable(VeloQueryProvider provider, Expression expression)
    {
        _provider = provider;
        _expression = expression;
    }

    public Type ElementType => typeof(T);
    public Expression Expression => _expression;
    public IQueryProvider Provider => _provider;

    /// <summary>The owning context (used by source-generated interceptors).</summary>
    internal VeloDbContext Context => _provider.Context;

    public IEnumerator<T> GetEnumerator() =>
        _provider.Execute<IEnumerable<T>>(_expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
