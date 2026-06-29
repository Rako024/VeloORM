using System.Data.Common;
using VeloORM.Materialization;

namespace VeloORM.Runtime.Materialization;

/// <summary>Adapts a compiled <c>Func&lt;DbDataReader, T&gt;</c> to <see cref="IMaterializer{T}"/>.</summary>
internal sealed class DelegateMaterializer<T>(Func<DbDataReader, T> materialize) : IMaterializer<T>
{
    public T Read(DbDataReader reader) => materialize(reader);
}
