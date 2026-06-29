using System.Reflection;

namespace VeloORM.Metadata;

/// <summary>Immutable description of a mapped column.</summary>
public sealed class ColumnModel
{
    public ColumnModel(
        PropertyInfo property,
        string columnName,
        Type clrType,
        bool isNullable,
        bool isKey,
        int keyOrder,
        StoreGenerated storeGenerated,
        string? storeType)
    {
        Property = property;
        ColumnName = columnName;
        ClrType = clrType;
        IsNullable = isNullable;
        IsKey = isKey;
        KeyOrder = keyOrder;
        StoreGenerated = storeGenerated;
        StoreType = storeType;
    }

    /// <summary>The CLR property this column maps to.</summary>
    public PropertyInfo Property { get; }

    /// <summary>The CLR property name (convenience).</summary>
    public string PropertyName => Property.Name;

    /// <summary>The store column name.</summary>
    public string ColumnName { get; }

    /// <summary>The underlying CLR type with <see cref="Nullable{T}"/> unwrapped.</summary>
    public Type ClrType { get; }

    public bool IsNullable { get; }

    public bool IsKey { get; }

    /// <summary>Ordinal within a composite key (0-based).</summary>
    public int KeyOrder { get; }

    public StoreGenerated StoreGenerated { get; }

    /// <summary>Explicit store type if configured (e.g. <c>varchar(200)</c>), else null (dialect maps it).</summary>
    public string? StoreType { get; }
}
