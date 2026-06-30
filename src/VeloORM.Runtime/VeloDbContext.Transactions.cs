namespace VeloORM.Runtime;

/// <summary>
/// Transaction control. Both entry points open a dedicated connection from the factory and begin a
/// database transaction; the returned handle owns that connection for the transaction's lifetime.
/// </summary>
public partial class VeloDbContext
{
    /// <summary>Begins a transaction asynchronously. Use with <c>await using</c> so the handle (a
    /// <see cref="VeloTransaction"/> struct) rolls back automatically if not committed.</summary>
    public async Task<VeloTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var connection = ConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return new VeloTransaction(this, connection, transaction);
    }

    /// <summary>Begins a transaction synchronously. Use with <c>using</c>; the returned
    /// <see cref="VeloTransactionScope"/> is a stack-only ref struct.</summary>
    public VeloTransactionScope BeginTransaction()
    {
        var connection = ConnectionFactory.CreateConnection();
        connection.Open();
        var transaction = connection.BeginTransaction();
        return new VeloTransactionScope(connection, transaction);
    }
}
