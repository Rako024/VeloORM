namespace VeloORM.Metadata;

/// <summary>Maps an entity type to a database table (and optional schema).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;

    /// <summary>The table name.</summary>
    public string Name { get; }

    /// <summary>Optional schema name (e.g. <c>public</c>).</summary>
    public string? Schema { get; set; }
}

/// <summary>Maps a property to a specific column name.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(string name) => Name = name;

    public string Name { get; }

    /// <summary>Optional explicit store type (e.g. <c>varchar(200)</c>). Overrides dialect mapping.</summary>
    public string? TypeName { get; set; }
}

/// <summary>Marks a property as part of the primary key. Multiple properties form a composite key.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class KeyAttribute : Attribute
{
    /// <summary>Ordinal within a composite key (ascending). Defaults to declaration order.</summary>
    public int Order { get; set; }
}

/// <summary>Excludes a property from mapping.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class NotMappedAttribute : Attribute;

/// <summary>
/// Marks an entity that has a model-level query filter (configured via
/// <c>EntityTypeBuilder.HasQueryFilter</c>). This compile-time-visible marker tells the source
/// generator to leave queries on this entity to the runtime engine (which applies the filter) instead
/// of intercepting them with baked SQL that cannot see the filter. Required for filter correctness on
/// the interceptor path; ignored by the runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class VeloQueryFilterAttribute : Attribute;

/// <summary>
/// Names the foreign-key property for a navigation. Applied to a navigation property, the name is the
/// scalar FK property on the dependent entity (e.g. <c>[ForeignKey("CustomerId")]</c> on
/// <c>Order.Customer</c>, or on <c>Customer.Orders</c> naming <c>Order.CustomerId</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ForeignKeyAttribute : Attribute
{
    public ForeignKeyAttribute(string name) => Name = name;

    /// <summary>The FK property name (resolved to its column).</summary>
    public string Name { get; }
}

/// <summary>How a column's value is produced by the store.</summary>
public enum StoreGenerated
{
    /// <summary>The application always provides the value.</summary>
    Never = 0,

    /// <summary>The store generates the value on insert (e.g. identity / serial).</summary>
    OnAdd = 1,

    /// <summary>The store generates the value on insert and update (e.g. <c>xmin</c>, timestamps).</summary>
    OnAddOrUpdate = 2,
}

/// <summary>Declares how the store generates a column's value.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class DatabaseGeneratedAttribute : Attribute
{
    public DatabaseGeneratedAttribute(StoreGenerated option) => Option = option;

    public StoreGenerated Option { get; }
}

/// <summary>Declares an index over one or more columns. Apply once per index on the entity type.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class IndexAttribute : Attribute
{
    public IndexAttribute(params string[] propertyNames) => PropertyNames = propertyNames;

    /// <summary>Names of the properties (not columns) covered by the index, in order.</summary>
    public string[] PropertyNames { get; }

    /// <summary>Optional explicit index name. Defaults to a generated name.</summary>
    public string? Name { get; set; }

    public bool IsUnique { get; set; }
}
