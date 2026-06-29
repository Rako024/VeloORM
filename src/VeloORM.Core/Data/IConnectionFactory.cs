using System.Data.Common;

namespace VeloORM.Data;

/// <summary>
/// Creates ready-to-open <see cref="DbConnection"/> instances. Implemented per provider
/// (the PostgreSQL implementation simply hands out pooled Npgsql connections — VeloORM does
/// not implement its own connection pool).
/// </summary>
public interface IConnectionFactory
{
    /// <summary>Creates a new, unopened connection. Pooling is the provider/driver's concern.</summary>
    DbConnection CreateConnection();
}
