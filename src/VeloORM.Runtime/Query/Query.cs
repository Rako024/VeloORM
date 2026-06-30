namespace VeloORM.Runtime;

/// <summary>
/// Explicit opt-in for compiled queries. <c>Query.Compile</c> returns a reusable delegate; storing and
/// re-invoking it keeps hitting the warm shape-keyed cache (a compiled-query handle). The source
/// generator always recognizes these calls — if the supplied query is not a statically analyzable
/// query rooted at <c>db.Set&lt;T&gt;()</c>, it reports the compile error <c>VELO002</c> rather than
/// silently doing nothing.
/// </summary>
public static class Query
{
    public static Func<TContext, TResult> Compile<TContext, TResult>(Func<TContext, TResult> query)
        where TContext : VeloDbContext => query;

    public static Func<TContext, T1, TResult> Compile<TContext, T1, TResult>(Func<TContext, T1, TResult> query)
        where TContext : VeloDbContext => query;

    public static Func<TContext, T1, T2, TResult> Compile<TContext, T1, T2, TResult>(Func<TContext, T1, T2, TResult> query)
        where TContext : VeloDbContext => query;
}
