namespace VeloORM.Metadata;

/// <summary>Classifies CLR types as scalar (mappable to a single column) or not.</summary>
public static class TypeSupport
{
    private static readonly HashSet<Type> ScalarTypes =
    [
        typeof(string), typeof(bool), typeof(byte), typeof(sbyte),
        typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double),
        typeof(decimal), typeof(char),
        typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
        typeof(DateOnly), typeof(TimeOnly),
        typeof(Guid), typeof(byte[]),
    ];

    /// <summary>True if <paramref name="type"/> (with nullable already unwrapped) maps to one column.</summary>
    public static bool IsScalar(Type type) =>
        ScalarTypes.Contains(type) || type.IsEnum;
}
