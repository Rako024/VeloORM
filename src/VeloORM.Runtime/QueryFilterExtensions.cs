using System.Linq.Expressions;
using System.Reflection;

namespace VeloORM.Runtime;

/// <summary>
/// Opt-out for model-level query filters. <c>db.Set&lt;T&gt;().IgnoreQueryFilters()</c> appends a marker
/// the runtime translator recognizes to skip the entity's <c>HasQueryFilter</c> predicate for this
/// query (e.g. to include soft-deleted rows in an admin view).
/// </summary>
public static class QueryFilterExtensions
{
    internal static readonly MethodInfo IgnoreMethod =
        typeof(QueryFilterExtensions).GetMethod(nameof(IgnoreQueryFilters))!;

    public static IQueryable<T> IgnoreQueryFilters<T>(this IQueryable<T> source) =>
        source.Provider.CreateQuery<T>(
            Expression.Call(null, IgnoreMethod.MakeGenericMethod(typeof(T)), source.Expression));
}
