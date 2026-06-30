using System.Diagnostics.CodeAnalysis;
using VeloORM.Data;
using VeloORM.Metadata;
using VeloORM.Runtime.Internal;
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

    /// <summary>Routes every executed command's parameterized SQL to <paramref name="sink"/>
    /// (e.g. <c>db.LogTo(Console.WriteLine)</c>). One delegate is stored — no per-query allocation —
    /// and bound values never appear in the text, so logging is injection/leak-safe by construction.</summary>
    public VeloDbContext LogTo(Action<string> sink)
    {
        Executor.CommandLogger = sink ?? throw new ArgumentNullException(nameof(sink));
        return this;
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
