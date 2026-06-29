using System.Diagnostics.CodeAnalysis;
using VeloORM.Data;
using VeloORM.Metadata;
using VeloORM.Runtime.Query;
using VeloORM.Sql;

namespace VeloORM.Runtime;

/// <summary>
/// The concrete, executing VeloORM context. Holds the resolved model, dialect, connection factory,
/// and the process-wide shape-keyed query cache. Subclass it and expose <c>IQueryable&lt;T&gt;</c>
/// properties, or call <see cref="Set{TEntity}"/> directly.
/// </summary>
public partial class VeloDbContext : IDbContext
{
    private readonly QueryEngine _engine;

    [RequiresUnreferencedCode("Runtime model building and materialization reflect over entity types. " +
        "For trimming/AOT, use the source-generated model and materializers.")]
    public VeloDbContext(
        VeloModel model,
        ISqlDialect dialect,
        IConnectionFactory connectionFactory,
        ICommandExecutor executor)
    {
        Model = model;
        Dialect = dialect;
        ConnectionFactory = connectionFactory;
        Executor = executor;
        _engine = new QueryEngine(this);
    }

    public VeloModel Model { get; }

    public ISqlDialect Dialect { get; }

    public IConnectionFactory ConnectionFactory { get; }

    internal ICommandExecutor Executor { get; }

    /// <summary>Number of cache misses that required full query compilation (SQL render + materializer
    /// build). Stays constant when the same query shape is re-executed with different values.</summary>
    public long QueryCompilationCount => _engine.CompilationCount;

    internal QueryEngine Engine => _engine;

    public IQueryable<TEntity> Set<TEntity>() where TEntity : class
    {
        // Ensure the type is part of the model up front for a clear error.
        _ = Model.GetEntity(typeof(TEntity));
        return new VeloQueryable<TEntity>(new VeloQueryProvider(this));
    }

    public void Dispose()
    {
        (ConnectionFactory as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
