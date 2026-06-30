namespace VeloORM.Metadata;

/// <summary>Immutable mapping description for one entity type.</summary>
public sealed class EntityModel
{
    private readonly Dictionary<string, ColumnModel> _byProperty;

    public EntityModel(
        Type clrType,
        string tableName,
        string? schema,
        IReadOnlyList<ColumnModel> columns,
        IReadOnlyList<IndexModel> indexes)
    {
        ClrType = clrType;
        TableName = tableName;
        Schema = schema;
        Columns = columns;
        Indexes = indexes;
        KeyColumns = columns.Where(c => c.IsKey).OrderBy(c => c.KeyOrder).ToArray();
        _byProperty = columns.ToDictionary(c => c.PropertyName, StringComparer.Ordinal);
    }

    /// <summary>Navigations to other entities. Populated by the navigation resolver after every
    /// entity's columns are known (a navigation needs the target's table/key).</summary>
    public IReadOnlyList<NavigationModel> Navigations { get; internal set; } = Array.Empty<NavigationModel>();

    public NavigationModel? FindNavigation(string propertyName) =>
        Navigations.FirstOrDefault(n => string.Equals(n.PropertyName, propertyName, StringComparison.Ordinal));

    /// <summary>A model-level filter (e.g. soft delete) auto-applied as a root WHERE by the runtime
    /// translator unless ignored. Null when none is configured.</summary>
    public System.Linq.Expressions.LambdaExpression? QueryFilter { get; internal set; }

    public Type ClrType { get; }

    public string TableName { get; }

    public string? Schema { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public IReadOnlyList<ColumnModel> KeyColumns { get; }

    public IReadOnlyList<IndexModel> Indexes { get; }

    /// <summary>True when the entity has at least one key column.</summary>
    public bool HasKey => KeyColumns.Count > 0;

    public ColumnModel? FindColumnByProperty(string propertyName) =>
        _byProperty.TryGetValue(propertyName, out var c) ? c : null;

    public ColumnModel GetColumnByProperty(string propertyName) =>
        FindColumnByProperty(propertyName)
        ?? throw new InvalidOperationException(
            $"Property '{propertyName}' is not a mapped column on '{ClrType.Name}'.");
}
