using VeloORM.Materialization;
using VeloORM.Query;

namespace VeloORM.Data;

/// <summary>
/// Executes rendered <see cref="SqlStatement"/>s against the database. Provider-specific
/// (the PostgreSQL implementation binds Npgsql positional parameters); the runtime engine
/// depends only on this abstraction so it stays provider-agnostic.
/// </summary>
public interface ICommandExecutor
{
    List<T> Query<T>(SqlStatement statement, IMaterializer<T> materializer);
    int Execute(SqlStatement statement);
    TScalar? ExecuteScalar<TScalar>(SqlStatement statement);

    Task<List<T>> QueryAsync<T>(SqlStatement statement, IMaterializer<T> materializer, CancellationToken cancellationToken = default);
    Task<int> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken = default);
    Task<TScalar?> ExecuteScalarAsync<TScalar>(SqlStatement statement, CancellationToken cancellationToken = default);
}
