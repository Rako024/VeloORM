using System.Data.Common;
using VeloORM.Data;
using VeloORM.Query;
using VeloORM.Runtime.Materialization;
using VeloORM.Runtime.Internal;

namespace VeloORM.Runtime;

/// <summary>
/// Public runtime support invoked by source-generated interceptors. The generator bakes the SQL and a
/// reflection-free materializer at compile time and calls these helpers, which obtain the context from
/// the intercepted query and execute directly — bypassing expression translation entirely. The
/// generated code lives in the consuming assembly, so this surface must be public.
/// </summary>
public static class VeloInterceptorSupport
{
    public static List<T> ExecuteList<T>(
        IQueryable<T> source, string sql, SqlParameterBinding[] parameters, Func<DbDataReader, T> materializer)
    {
        var context = ContextOf(source);
        return context.Executor.Query(new SqlStatement(sql, parameters), new DelegateMaterializer<T>(materializer));
    }

    public static T ExecuteFirst<T>(
        IQueryable<T> source, string sql, SqlParameterBinding[] parameters, Func<DbDataReader, T> materializer, bool orDefault)
    {
        var rows = ExecuteList(source, sql, parameters, materializer);
        if (rows.Count > 0) return rows[0];
        if (orDefault) return default!;
        throw new InvalidOperationException("Sequence contains no elements.");
    }

    public static T ExecuteSingle<T>(
        IQueryable<T> source, string sql, SqlParameterBinding[] parameters, Func<DbDataReader, T> materializer, bool orDefault)
    {
        var rows = ExecuteList(source, sql, parameters, materializer);
        if (rows.Count > 1) throw new InvalidOperationException("Sequence contains more than one element.");
        if (rows.Count == 0) return orDefault ? default! : throw new InvalidOperationException("Sequence contains no elements.");
        return rows[0];
    }

    public static int ExecuteCount<T>(IQueryable<T> source, string sql, SqlParameterBinding[] parameters)
    {
        var context = ContextOf(source);
        return checked((int)context.Executor.ExecuteScalar<long>(new SqlStatement(sql, parameters)));
    }

    public static bool ExecuteAny<T>(IQueryable<T> source, string sql, SqlParameterBinding[] parameters)
    {
        var context = ContextOf(source);
        return context.Executor.ExecuteScalar<bool>(new SqlStatement(sql, parameters));
    }

    /// <summary>Executes a scalar aggregate (<c>sum/avg/min/max</c>) and coerces the value to the
    /// LINQ-declared <typeparamref name="TResult"/>, matching LINQ's empty-sequence semantics:
    /// <c>Sum</c> over no rows yields 0 (<paramref name="sumZeroIfEmpty"/>); <c>Min/Max/Average</c>
    /// over no rows return null for a nullable result, otherwise throw.</summary>
    public static TResult ExecuteAggregate<T, TResult>(
        IQueryable<T> source, string sql, SqlParameterBinding[] parameters, bool sumZeroIfEmpty)
    {
        var context = ContextOf(source);
        var raw = context.Executor.ExecuteScalar<object>(new SqlStatement(sql, parameters));

        if (raw is null)
        {
            if (default(TResult) is null) return default!;       // nullable value type or reference type
            if (sumZeroIfEmpty) return default!;                 // Sum over empty == 0
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        var target = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
        return (TResult)Convert.ChangeType(raw, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- compiled-query (parameterized, boxing-free) helpers ----------
    // The binder adds the lambda's typed parameters via NpgsqlParameter<T>, so value types are never
    // boxed. SQL is baked at compile time.

    public static List<T> ExecuteListBound<T>(
        IQueryable<T> source, string sql, Func<DbDataReader, T> materializer, Action<ITypedParameterSink> bind) =>
        ContextOf(source).Executor.QueryBound(sql, new DelegateMaterializer<T>(materializer), bind);

    public static T ExecuteFirstBound<T>(
        IQueryable<T> source, string sql, Func<DbDataReader, T> materializer, Action<ITypedParameterSink> bind, bool orDefault)
    {
        var rows = ExecuteListBound(source, sql, materializer, bind);
        if (rows.Count > 0) return rows[0];
        if (orDefault) return default!;
        throw new InvalidOperationException("Sequence contains no elements.");
    }

    public static T ExecuteSingleBound<T>(
        IQueryable<T> source, string sql, Func<DbDataReader, T> materializer, Action<ITypedParameterSink> bind, bool orDefault)
    {
        var rows = ExecuteListBound(source, sql, materializer, bind);
        if (rows.Count > 1) throw new InvalidOperationException("Sequence contains more than one element.");
        if (rows.Count == 0) return orDefault ? default! : throw new InvalidOperationException("Sequence contains no elements.");
        return rows[0];
    }

    public static int ExecuteCountBound<T>(IQueryable<T> source, string sql, Action<ITypedParameterSink> bind) =>
        checked((int)ContextOf(source).Executor.ExecuteScalarBound<long>(sql, bind));

    public static bool ExecuteAnyBound<T>(IQueryable<T> source, string sql, Action<ITypedParameterSink> bind) =>
        ContextOf(source).Executor.ExecuteScalarBound<bool>(sql, bind);

    private static VeloDbContext ContextOf<T>(IQueryable<T> source) =>
        source is VeloQueryable<T> q
            ? q.Context
            : throw new InvalidOperationException("Intercepted source is not a VeloORM query.");
}
