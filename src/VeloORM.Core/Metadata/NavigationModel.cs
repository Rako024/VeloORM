using System.Reflection;

namespace VeloORM.Metadata;

public enum NavigationKind
{
    /// <summary>Many-to-one: this entity holds the foreign key (e.g. <c>Order.User</c>).</summary>
    Reference,
    /// <summary>One-to-many: the target entity holds the foreign key (e.g. <c>User.Orders</c>).</summary>
    Collection,
    /// <summary>Many-to-many through a junction (pivot) table (e.g. <c>Post.Tags</c> via <c>post_tags</c>).</summary>
    ManyToMany,
}

/// <summary>
/// Describes a navigation between two mapped entities. The relationship is expressed as an equality
/// between a column on the entity declaring the navigation (<see cref="LocalKeyColumnName"/>) and a
/// column on the target entity (<see cref="TargetKeyColumnName"/>):
/// <list type="bullet">
/// <item>Reference: local = FK on this entity, target = principal key on the target.</item>
/// <item>Collection: local = principal key on this entity, target = FK on the target.</item>
/// </list>
/// </summary>
public sealed class NavigationModel
{
    public NavigationModel(
        PropertyInfo property,
        NavigationKind kind,
        Type targetClrType,
        string localKeyColumnName,
        string targetKeyColumnName)
    {
        Property = property;
        Kind = kind;
        TargetClrType = targetClrType;
        LocalKeyColumnName = localKeyColumnName;
        TargetKeyColumnName = targetKeyColumnName;
    }

    public PropertyInfo Property { get; }
    public string PropertyName => Property.Name;
    public NavigationKind Kind { get; }
    public Type TargetClrType { get; }

    /// <summary>Column on the entity that declares the navigation.</summary>
    public string LocalKeyColumnName { get; }

    /// <summary>Column on the target entity.</summary>
    public string TargetKeyColumnName { get; }

    // ---- many-to-many junction (set only when Kind == ManyToMany) ----

    public string? JunctionSchema { get; private set; }
    /// <summary>The junction (pivot) table name.</summary>
    public string? JunctionTable { get; private set; }
    /// <summary>Junction column referencing this entity's key (<see cref="LocalKeyColumnName"/>).</summary>
    public string? JunctionLocalKeyColumn { get; private set; }
    /// <summary>Junction column referencing the target entity's key (<see cref="TargetKeyColumnName"/>).</summary>
    public string? JunctionTargetKeyColumn { get; private set; }

    /// <summary>Builds a many-to-many navigation through an explicit junction table.</summary>
    public static NavigationModel ManyToManyNav(
        PropertyInfo property, Type targetClrType,
        string localKeyColumnName, string targetKeyColumnName,
        string? junctionSchema, string junctionTable,
        string junctionLocalKeyColumn, string junctionTargetKeyColumn) =>
        new(property, NavigationKind.ManyToMany, targetClrType, localKeyColumnName, targetKeyColumnName)
        {
            JunctionSchema = junctionSchema,
            JunctionTable = junctionTable,
            JunctionLocalKeyColumn = junctionLocalKeyColumn,
            JunctionTargetKeyColumn = junctionTargetKeyColumn,
        };
}
