using System.Linq.Expressions;
using System.Reflection;

namespace VeloORM.Runtime;

/// <summary>
/// Eager-loading extensions. <c>Include</c> appends a marker call to the query expression that the
/// runtime translator recognizes: a reference navigation becomes a <c>LEFT JOIN</c>; a collection
/// navigation is loaded by a second query and stitched by foreign key.
/// </summary>
public static class VeloQueryableExtensions
{
    public static IQueryable<TEntity> Include<TEntity, TProperty>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TProperty>> navigation)
    {
        var method = ((MethodInfo)MethodBase.GetCurrentMethod()!).MakeGenericMethod(typeof(TEntity), typeof(TProperty));
        return source.Provider.CreateQuery<TEntity>(
            Expression.Call(null, method, source.Expression, Expression.Quote(navigation)));
    }
}
