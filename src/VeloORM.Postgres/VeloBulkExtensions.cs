using System.Diagnostics.CodeAnalysis;
using Npgsql;
using VeloORM.Runtime;

namespace VeloORM.Postgres;

/// <summary>
/// Ergonomic bulk entry points on <see cref="VeloDbContext"/> (PostgreSQL-specific, hence extension
/// methods here rather than on the provider-agnostic context). Both use binary <c>COPY</c>:
/// <see cref="BulkInsert{T}"/> writes straight to the table; <see cref="BulkUpdate{T}(VeloDbContext, IEnumerable{T})"/>
/// stages into a temp table and applies one <c>UPDATE … FROM</c>.
/// </summary>
public static class VeloBulkExtensions
{
    [RequiresUnreferencedCode("Reads entity property values via reflection.")]
    public static ulong BulkInsert<T>(this VeloDbContext context, IEnumerable<T> rows)
    {
        var model = context.Model.GetEntity(typeof(T));
        return new PostgresBulkInserter(context.ConnectionFactory).Copy(model, rows);
    }

    [RequiresUnreferencedCode("Reads entity property values via reflection.")]
    public static ulong BulkUpdate<T>(this VeloDbContext context, IEnumerable<T> rows)
    {
        var model = context.Model.GetEntity(typeof(T));
        return new PostgresBulkUpdater(context.ConnectionFactory).Update(model, rows);
    }

    /// <summary>Bulk-updates within an existing transaction (staged temp table + <c>UPDATE … FROM</c>
    /// run on the transaction's connection).</summary>
    [RequiresUnreferencedCode("Reads entity property values via reflection.")]
    public static ulong BulkUpdate<T>(this VeloDbContext context, IEnumerable<T> rows, VeloTransaction transaction)
    {
        var model = context.Model.GetEntity(typeof(T));
        return new PostgresBulkUpdater(context.ConnectionFactory)
            .Update(model, rows, (NpgsqlTransaction)transaction.Transaction);
    }
}
