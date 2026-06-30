namespace VeloORM.Data;

/// <summary>
/// A sink that binds parameters by their concrete CLR type. The generated compiled-query path calls
/// <see cref="Add{T}"/> with strongly-typed values, letting the provider create a typed parameter
/// (e.g. <c>NpgsqlParameter&lt;T&gt;</c>) with no boxing of value types.
/// </summary>
public interface ITypedParameterSink
{
    /// <summary>Binds the next positional parameter (<c>$1</c>, <c>$2</c>, … in call order).</summary>
    void Add<T>(T value);
}
