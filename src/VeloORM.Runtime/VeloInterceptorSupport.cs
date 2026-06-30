using System.Data.Common;
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

    private static VeloDbContext ContextOf<T>(IQueryable<T> source) =>
        source is VeloQueryable<T> q
            ? q.Context
            : throw new InvalidOperationException("Intercepted source is not a VeloORM query.");
}
