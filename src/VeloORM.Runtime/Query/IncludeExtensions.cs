using System.Linq.Expressions;
using System.Reflection;

namespace VeloORM.Runtime;

/// <summary>
/// A query that has just had a navigation <c>Include</c>d, carrying the included type so
/// <c>ThenInclude</c> can chain onto it (EF-style). It is an ordinary <see cref="IQueryable{T}"/> —
/// only the generic <typeparamref name="TProperty"/> guides overload resolution.
/// </summary>
public interface IIncludableQueryable<out TEntity, out TProperty> : IQueryable<TEntity>
{
}

/// <summary>
/// Eager-loading extensions. <c>Include</c>/<c>ThenInclude</c> append marker calls to the query
/// expression that the runtime translator recognizes: a reference navigation becomes a <c>LEFT JOIN</c>;
/// a collection navigation is loaded by a follow-up query and stitched by foreign key.
/// </summary>
public static class VeloQueryableExtensions
{
    private static readonly MethodInfo IncludeMethod =
        typeof(VeloQueryableExtensions).GetMethod(nameof(Include))!;
    private static readonly MethodInfo ThenIncludeRefMethod =
        typeof(VeloQueryableExtensions).GetMethods()
            .First(m => m.Name == nameof(ThenInclude) && IsReferenceOverload(m));
    private static readonly MethodInfo ThenIncludeCollectionMethod =
        typeof(VeloQueryableExtensions).GetMethods()
            .First(m => m.Name == nameof(ThenInclude) && !IsReferenceOverload(m));

    public static IIncludableQueryable<TEntity, TProperty> Include<TEntity, TProperty>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TProperty>> navigation)
    {
        var method = IncludeMethod.MakeGenericMethod(typeof(TEntity), typeof(TProperty));
        var query = source.Provider.CreateQuery<TEntity>(
            Expression.Call(null, method, source.Expression, Expression.Quote(navigation)));
        return new IncludableQueryable<TEntity, TProperty>(query);
    }

    /// <summary>Chains onto the previously included <em>reference</em> navigation.</summary>
    public static IIncludableQueryable<TEntity, TProperty> ThenInclude<TEntity, TPrevious, TProperty>(
        this IIncludableQueryable<TEntity, TPrevious> source,
        Expression<Func<TPrevious, TProperty>> navigation)
    {
        var method = ThenIncludeRefMethod.MakeGenericMethod(typeof(TEntity), typeof(TPrevious), typeof(TProperty));
        var query = source.Provider.CreateQuery<TEntity>(
            Expression.Call(null, method, source.Expression, Expression.Quote(navigation)));
        return new IncludableQueryable<TEntity, TProperty>(query);
    }

    /// <summary>Chains onto the previously included <em>collection</em> navigation (element type
    /// <typeparamref name="TPrevious"/>).</summary>
    public static IIncludableQueryable<TEntity, TProperty> ThenInclude<TEntity, TPrevious, TProperty>(
        this IIncludableQueryable<TEntity, IEnumerable<TPrevious>?> source,
        Expression<Func<TPrevious, TProperty>> navigation)
    {
        var method = ThenIncludeCollectionMethod.MakeGenericMethod(typeof(TEntity), typeof(TPrevious), typeof(TProperty));
        var query = source.Provider.CreateQuery<TEntity>(
            Expression.Call(null, method, source.Expression, Expression.Quote(navigation)));
        return new IncludableQueryable<TEntity, TProperty>(query);
    }

    private static bool IsReferenceOverload(MethodInfo m)
    {
        // The collection overload's second parameter is Expression<Func<...>> over IEnumerable<TPrev>.
        var sourceParam = m.GetParameters()[0].ParameterType; // IIncludableQueryable<TEntity, TPrev-or-IEnumerable<TPrev>>
        var prev = sourceParam.GetGenericArguments()[1];
        return !(prev.IsGenericType && prev.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }
}

/// <summary>Wraps an <see cref="IQueryable{T}"/> so <c>ThenInclude</c> is statically targetable while
/// every query operation delegates to the underlying VeloORM provider/expression.</summary>
internal sealed class IncludableQueryable<TEntity, TProperty> : IIncludableQueryable<TEntity, TProperty>
{
    private readonly IQueryable<TEntity> _inner;

    public IncludableQueryable(IQueryable<TEntity> inner) => _inner = inner;

    public Type ElementType => _inner.ElementType;
    public Expression Expression => _inner.Expression;
    public IQueryProvider Provider => _inner.Provider;
    public IEnumerator<TEntity> GetEnumerator() => _inner.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
}
