using System.Data.Common;

namespace VeloORM.Materialization;

/// <summary>
/// Maps a single current row of a <see cref="DbDataReader"/> to an instance of <typeparamref name="T"/>.
/// The interface is reflection-free; the source-generated path emits a sealed implementation, while the
/// runtime fallback compiles one via expression trees and caches it.
/// </summary>
public interface IMaterializer<out T>
{
    /// <summary>Reads the reader's current row into a new <typeparamref name="T"/>. Does not advance the reader.</summary>
    T Read(DbDataReader reader);
}

/// <summary>Delegate form of a row materializer, used as the cached value in the shape-keyed query cache.</summary>
public delegate T RowMaterializer<out T>(DbDataReader reader);
