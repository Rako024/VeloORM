namespace VeloORM.Runtime;

/// <summary>
/// A design-time factory for creating a <typeparamref name="TContext"/> without running the
/// application's normal startup. The <c>velo</c> CLI (add-migration / update-database / scaffold)
/// discovers and calls an implementation of this interface when it needs a context instance,
/// mirroring EF Core's <c>IDesignTimeDbContextFactory&lt;T&gt;</c>.
/// </summary>
/// <remarks>
/// Implement this when your context has no public <c>(string connectionString)</c> constructor —
/// for example when it is created through dependency injection. The implementation must be public
/// and have a public parameterless constructor so the CLI can instantiate it. If present, it takes
/// precedence over the constructor conventions the CLI otherwise uses.
/// </remarks>
/// <typeparam name="TContext">The context type this factory creates.</typeparam>
public interface IVeloDesignTimeDbContextFactory<out TContext>
    where TContext : VeloDbContext
{
    /// <summary>Creates a new context instance. <paramref name="args"/> carries any extra CLI
    /// arguments (currently unused; reserved for parity with EF Core and future options).</summary>
    TContext CreateDbContext(string[] args);
}
