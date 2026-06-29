using VeloORM.Data;
using VeloORM.Metadata;
using VeloORM.Sql;

namespace VeloORM;

/// <summary>
/// The root abstraction a VeloORM context exposes: the resolved entity model, the active SQL
/// dialect, a connection factory, and the type-safe query entry point. The concrete, executing
/// context lives in VeloORM.Runtime; the interface lives here so Core stays provider-agnostic.
/// </summary>
public interface IDbContext : IDisposable, IAsyncDisposable
{
    /// <summary>The resolved metadata for every entity in this context.</summary>
    VeloModel Model { get; }

    /// <summary>The dialect used to render SQL.</summary>
    ISqlDialect Dialect { get; }

    /// <summary>The factory used to obtain database connections.</summary>
    IConnectionFactory ConnectionFactory { get; }

    /// <summary>Type-safe query entry point for an entity type (the DbSet&lt;T&gt; equivalent).</summary>
    IQueryable<TEntity> Set<TEntity>() where TEntity : class;
}
