using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using VeloORM.Query;

namespace VeloORM.Runtime;

/// <summary>
/// A lightweight, allocation-light transaction handle over an open connection and an
/// <see cref="DbTransaction"/>. It is a <c>readonly struct</c> implementing
/// <see cref="IAsyncDisposable"/>, so <c>await using var tx = await db.BeginTransactionAsync()</c>
/// allocates nothing on the managed heap for the handle itself (pattern-based disposal — no boxing).
/// Commands run via this handle execute on the transaction's connection. If the handle is disposed
/// without <see cref="CommitAsync"/>, the transaction is rolled back (standard ADO.NET semantics:
/// disposing an uncommitted transaction rolls it back).
/// </summary>
public readonly struct VeloTransaction : IAsyncDisposable
{
    private readonly VeloDbContext _context;
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;

    internal VeloTransaction(VeloDbContext context, DbConnection connection, DbTransaction transaction)
    {
        _context = context;
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>The underlying open connection (for advanced/provider-specific operations, e.g. bulk).</summary>
    public DbConnection Connection => _connection;

    /// <summary>The underlying database transaction.</summary>
    public DbTransaction Transaction => _transaction;

    internal VeloDbContext Context => _context;

    public Task CommitAsync(CancellationToken cancellationToken = default) => _transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) => _transaction.RollbackAsync(cancellationToken);

    /// <summary>Runs a parameterized raw statement on this transaction. Interpolated values are bound
    /// (never inlined), so this remains injection-safe.</summary>
    public int Execute([InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql) =>
        _context.Executor.Execute(sql.ToStatement(), _transaction);

    public Task<int> ExecuteAsync(
        [InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql,
        CancellationToken cancellationToken = default) =>
        _context.Executor.ExecuteAsync(sql.ToStatement(), _transaction, cancellationToken);

    [RequiresUnreferencedCode("Builds a materializer via reflection for the raw-SQL result type.")]
    public List<T> Query<T>([InterpolatedStringHandlerArgument("")] VeloInterpolatedSql sql) =>
        _context.Executor.Query(sql.ToStatement(), _context.RawMaterializerFor<T>(), _transaction);

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted transaction rolls it back; a committed one is a no-op.
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Synchronous, stack-only (<c>ref struct</c>) transaction handle for <c>using</c> blocks. Because it
/// is a ref struct it can never be boxed or captured on the heap. Disposal without <see cref="Commit"/>
/// rolls the transaction back.
/// </summary>
public ref struct VeloTransactionScope
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;

    internal VeloTransactionScope(DbConnection connection, DbTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public readonly DbConnection Connection => _connection;
    public readonly DbTransaction Transaction => _transaction;

    public readonly void Commit() => _transaction.Commit();
    public readonly void Rollback() => _transaction.Rollback();

    public readonly void Dispose()
    {
        _transaction.Dispose(); // rolls back if not committed
        _connection.Dispose();
    }
}
