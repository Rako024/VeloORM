using System.Data.Common;
using Npgsql;
using VeloORM.Data;

namespace VeloORM.Postgres;

/// <summary>
/// Hands out Npgsql connections. Connection pooling is delegated entirely to Npgsql
/// (keyed by connection string) — VeloORM does not implement its own pool.
/// </summary>
public sealed class NpgsqlConnectionFactory : IConnectionFactory, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    /// <summary>Creates a factory backed by a data source built from the connection string.</summary>
    public NpgsqlConnectionFactory(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
    }

    /// <summary>Creates a factory over an externally-owned data source (not disposed by this factory).</summary>
    public NpgsqlConnectionFactory(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        _ownsDataSource = false;
    }

    public DbConnection CreateConnection() => _dataSource.CreateConnection();

    public void Dispose()
    {
        if (_ownsDataSource)
            _dataSource.Dispose();
    }
}
