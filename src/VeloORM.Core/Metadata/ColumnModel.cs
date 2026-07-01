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
        string? storeType,
        bool normalizeToUtc = false)
    {
        Property = property;
        ColumnName = columnName;
        ClrType = clrType;
        IsNullable = isNullable;
        IsKey = isKey;
        KeyOrder = keyOrder;
        StoreGenerated = storeGenerated;
        StoreType = storeType;
        NormalizeToUtc = normalizeToUtc;
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

    /// <summary>When true (a <see cref="DateTime"/> column marked <c>[UtcDateTime]</c>, configured with
    /// <c>AsUtc()</c>, or under a model built with <c>NormalizeDateTimesToUtc</c>), materialized values
    /// are stamped <see cref="DateTimeKind.Utc"/> on read. Writes of any <see cref="DateTime"/> are
    /// always stored as <see cref="DateTimeKind.Unspecified"/> (matching the <c>timestamp</c> column
    /// type), independent of this flag.</summary>
    public bool NormalizeToUtc { get; }
}
