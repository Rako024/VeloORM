using System.Data.Common;
using VeloORM.Materialization;
using VeloORM.Query;

namespace VeloORM.Data;

/// <summary>
/// Executes rendered <see cref="SqlStatement"/>s against the database. Provider-specific
/// (the PostgreSQL implementation binds Npgsql positional parameters); the runtime engine
/// depends only on this abstraction so it stays provider-agnostic.
/// </summary>
/// <remarks>
/// Each method has a <see cref="DbTransaction"/> overload: when a transaction is supplied the command
/// runs on that transaction's (already-open) connection and the connection is left open; otherwise a
/// fresh pooled connection is opened and disposed per call.
/// </remarks>
public interface ICommandExecutor
{
    List<T> Query<T>(SqlStatement statement, IMaterializer<T> materializer);
    int Execute(SqlStatement statement);
    TScalar? ExecuteScalar<TScalar>(SqlStatement statement);

    Task<List<T>> QueryAsync<T>(SqlStatement statement, IMaterializer<T> materializer, CancellationToken cancellationToken = default);
    Task<int> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken = default);
    Task<TScalar?> ExecuteScalarAsync<TScalar>(SqlStatement statement, CancellationToken cancellationToken = default);

    // Transaction-aware overloads. A null transaction is equivalent to the parameterless form.
    List<T> Query<T>(SqlStatement statement, IMaterializer<T> materializer, DbTransaction? transaction);
    int Execute(SqlStatement statement, DbTransaction? transaction);
    TScalar? ExecuteScalar<TScalar>(SqlStatement statement, DbTransaction? transaction);

    Task<List<T>> QueryAsync<T>(SqlStatement statement, IMaterializer<T> materializer, DbTransaction? transaction, CancellationToken cancellationToken = default);
    Task<int> ExecuteAsync(SqlStatement statement, DbTransaction? transaction, CancellationToken cancellationToken = default);
    Task<TScalar?> ExecuteScalarAsync<TScalar>(SqlStatement statement, DbTransaction? transaction, CancellationToken cancellationToken = default);

    // Compiled-query path: SQL is baked at compile time and parameters are bound by concrete type via
    // the sink (no boxing of value types). The binder adds parameters in $1, $2, … order.
    List<T> QueryBound<T>(string sql, IMaterializer<T> materializer, Action<ITypedParameterSink> bindParameters);
    TScalar? ExecuteScalarBound<TScalar>(string sql, Action<ITypedParameterSink> bindParameters);
}
