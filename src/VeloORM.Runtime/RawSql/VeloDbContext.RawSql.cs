using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using VeloORM.Materialization;
using VeloORM.Query;
using VeloORM.Runtime.Materialization;
using VeloORM.Runtime.Query;

namespace VeloORM.Runtime;

/// <summary>
/// Raw-SQL escape hatch. All entry points take a <see cref="VeloInterpolatedSql"/>, so callers write
/// <c>db.Query&lt;Order&gt;($"SELECT * FROM get_orders({customerId}, {fromDate})")</c> and the
/// interpolated values are automatically bound — manual string concatenation cannot reach the SQL.
/// Use these for stored functions/procedures and views.
/// </summary>
public partial class VeloDbContext
{
    [RequiresUnreferencedCode("Builds a materializer via reflection for the raw-SQL result type.")]
    public List<T> Query<T>([InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql) =>
        Executor.Query(sql.ToStatement(), MaterializerFor<T>());

    [RequiresUnreferencedCode("Builds a materializer via reflection for the raw-SQL result type.")]
    public Task<List<T>> QueryAsync<T>(
        [InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql,
        CancellationToken cancellationToken = default) =>
        Executor.QueryAsync(sql.ToStatement(), MaterializerFor<T>(), cancellationToken);

    [RequiresUnreferencedCode("Builds a materializer via reflection for the raw-SQL result type.")]
    public T? QuerySingleOrDefault<T>([InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql)
    {
        var rows = Executor.Query(sql.ToStatement(), MaterializerFor<T>());
        if (rows.Count > 1)
            throw new InvalidOperationException("Sequence contains more than one element.");
        return rows.Count == 0 ? default : rows[0];
    }

    public int Execute([InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql) =>
        Executor.Execute(sql.ToStatement());

    public Task<int> ExecuteAsync(
        [InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql,
        CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(sql.ToStatement(), cancellationToken);

    public TScalar? ExecuteScalar<TScalar>([InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql) =>
        Executor.ExecuteScalar<TScalar>(sql.ToStatement());

    [RequiresUnreferencedCode("Builds a materializer via reflection for the raw-SQL result type.")]
    private IMaterializer<T> MaterializerFor<T>()
    {
        // Known entity type -> map columns by name; otherwise read a single scalar from column 0.
        if (Model.FindEntity(typeof(T)) is { } entity)
        {
            var plan = new ProjectionPlan { Kind = ProjectionKind.Entity, ResultType = typeof(T), Entity = entity };
            return new DelegateMaterializer<T>((Func<DbDataReader, T>)RuntimeMaterializerFactory.Build(plan));
        }

        return new DelegateMaterializer<T>((Func<DbDataReader, T>)RuntimeMaterializerFactory.BuildScalarByOrdinal(typeof(T)));
    }
}
