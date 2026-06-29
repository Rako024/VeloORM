using System.Linq.Expressions;
using System.Reflection;

namespace VeloORM.Metadata;

/// <summary>Mutable per-property fluent overrides, merged over attribute/convention defaults.</summary>
public sealed class PropertyConfiguration
{
    public string? ColumnName { get; set; }
    public string? StoreType { get; set; }
    public bool? IsRequired { get; set; }
    public StoreGenerated? StoreGenerated { get; set; }
    public bool Ignored { get; set; }
}

/// <summary>Mutable fluent overrides for one entity type.</summary>
public sealed class EntityConfiguration
{
    public Type ClrType { get; }
    public string? TableName { get; set; }
    public string? Schema { get; set; }
    public List<string>? KeyProperties { get; set; }
    public Dictionary<string, PropertyConfiguration> Properties { get; } = new(StringComparer.Ordinal);
    public List<IndexConfiguration> Indexes { get; } = new();

    public EntityConfiguration(Type clrType) => ClrType = clrType;

    public PropertyConfiguration PropertyFor(string name)
    {
        if (!Properties.TryGetValue(name, out var cfg))
            Properties[name] = cfg = new PropertyConfiguration();
        return cfg;
    }
}

/// <summary>Mutable fluent index declaration.</summary>
public sealed class IndexConfiguration
{
    public required IReadOnlyList<string> PropertyNames { get; init; }
    public string? Name { get; set; }
    public bool IsUnique { get; set; }
}

/// <summary>Root fluent model configuration. Subclass <see cref="VeloDbContextBase"/>-style
/// contexts call <c>OnModelCreating(ModelBuilder)</c> to populate this.</summary>
public sealed class ModelBuilder
{
    private readonly Dictionary<Type, EntityConfiguration> _entities = new();

    public EntityTypeBuilder<TEntity> Entity<TEntity>() where TEntity : class
    {
        if (!_entities.TryGetValue(typeof(TEntity), out var cfg))
            _entities[typeof(TEntity)] = cfg = new EntityConfiguration(typeof(TEntity));
        return new EntityTypeBuilder<TEntity>(cfg);
    }

    public EntityConfiguration? FindConfiguration(Type type) =>
        _entities.TryGetValue(type, out var cfg) ? cfg : null;

    public IReadOnlyCollection<EntityConfiguration> Configurations => _entities.Values;
}

/// <summary>Fluent builder for one entity type.</summary>
public sealed class EntityTypeBuilder<TEntity> where TEntity : class
{
    private readonly EntityConfiguration _cfg;

    public EntityTypeBuilder(EntityConfiguration cfg) => _cfg = cfg;

    public EntityTypeBuilder<TEntity> ToTable(string name, string? schema = null)
    {
        _cfg.TableName = name;
        _cfg.Schema = schema;
        return this;
    }

    public EntityTypeBuilder<TEntity> HasKey<TKey>(Expression<Func<TEntity, TKey>> keyExpression)
    {
        _cfg.KeyProperties = ExpressionHelpers.GetPropertyNames(keyExpression);
        return this;
    }

    public PropertyBuilder<TProp> Property<TProp>(Expression<Func<TEntity, TProp>> propertyExpression)
    {
        var name = ExpressionHelpers.GetSinglePropertyName(propertyExpression);
        return new PropertyBuilder<TProp>(_cfg.PropertyFor(name));
    }

    public EntityTypeBuilder<TEntity> Ignore<TProp>(Expression<Func<TEntity, TProp>> propertyExpression)
    {
        var name = ExpressionHelpers.GetSinglePropertyName(propertyExpression);
        _cfg.PropertyFor(name).Ignored = true;
        return this;
    }

    public EntityTypeBuilder<TEntity> HasIndex<TKey>(
        Expression<Func<TEntity, TKey>> indexExpression,
        bool unique = false,
        string? name = null)
    {
        _cfg.Indexes.Add(new IndexConfiguration
        {
            PropertyNames = ExpressionHelpers.GetPropertyNames(indexExpression),
            IsUnique = unique,
            Name = name,
        });
        return this;
    }
}

/// <summary>Fluent builder for one property.</summary>
public sealed class PropertyBuilder<TProp>
{
    private readonly PropertyConfiguration _cfg;

    public PropertyBuilder(PropertyConfiguration cfg) => _cfg = cfg;

    public PropertyBuilder<TProp> HasColumnName(string name) { _cfg.ColumnName = name; return this; }
    public PropertyBuilder<TProp> HasColumnType(string storeType) { _cfg.StoreType = storeType; return this; }
    public PropertyBuilder<TProp> IsRequired(bool required = true) { _cfg.IsRequired = required; return this; }
    public PropertyBuilder<TProp> ValueGeneratedOnAdd() { _cfg.StoreGenerated = Metadata.StoreGenerated.OnAdd; return this; }
    public PropertyBuilder<TProp> ValueGeneratedOnAddOrUpdate() { _cfg.StoreGenerated = Metadata.StoreGenerated.OnAddOrUpdate; return this; }
    public PropertyBuilder<TProp> ValueGeneratedNever() { _cfg.StoreGenerated = Metadata.StoreGenerated.Never; return this; }
}

/// <summary>Helpers to pull property names out of LINQ member-access lambdas.</summary>
internal static class ExpressionHelpers
{
    public static string GetSinglePropertyName(LambdaExpression expression)
    {
        var member = UnwrapMember(expression.Body)
            ?? throw new ArgumentException($"Expression '{expression}' must be a simple property access.");
        return member.Name;
    }

    public static List<string> GetPropertyNames(LambdaExpression expression)
    {
        // Supports both x => x.Prop and x => new { x.A, x.B } for composite keys/indexes.
        if (expression.Body is NewExpression newExpr)
        {
            var names = new List<string>(newExpr.Arguments.Count);
            foreach (var arg in newExpr.Arguments)
            {
                var m = UnwrapMember(arg)
                    ?? throw new ArgumentException($"Composite expression member '{arg}' must be a property access.");
                names.Add(m.Name);
            }
            return names;
        }

        var member = UnwrapMember(expression.Body)
            ?? throw new ArgumentException($"Expression '{expression}' must be a property access or anonymous projection.");
        return new List<string> { member.Name };
    }

    private static MemberInfo? UnwrapMember(Expression expression)
    {
        // Strip Convert nodes inserted for value types boxed to object.
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            expression = unary.Operand;
        return expression is MemberExpression { Member: PropertyInfo prop } ? prop : null;
    }
}
