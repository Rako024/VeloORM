using System.Linq.Expressions;

namespace VeloORM.Runtime.Query;

/// <summary>
/// The <see cref="IQueryProvider"/> for VeloORM's runtime engine. <see cref="CreateQuery"/> simply
/// wraps the growing expression tree; all real work happens in <see cref="Execute"/>, which hands the
/// expression to the <see cref="QueryEngine"/> for translation, caching, and execution.
/// </summary>
internal sealed class VeloQueryProvider : IQueryProvider
{
    public VeloQueryProvider(VeloDbContext context) => Context = context;

    public VeloDbContext Context { get; }

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = GetSequenceElementType(expression.Type);
        var queryableType = typeof(VeloQueryable<>).MakeGenericType(elementType);
        return (IQueryable)Activator.CreateInstance(queryableType, this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new VeloQueryable<TElement>(this, expression);

    public object? Execute(Expression expression) => Context.Engine.Execute(expression, expression.Type);

    public TResult Execute<TResult>(Expression expression) =>
        (TResult)Context.Engine.Execute(expression, typeof(TResult))!;

    private static Type GetSequenceElementType(Type type)
    {
        if (type.IsGenericType)
        {
            foreach (var i in type.GetInterfaces().Prepend(type))
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return i.GetGenericArguments()[0];
        }
        return type;
    }
}
