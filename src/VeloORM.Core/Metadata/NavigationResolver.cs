using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace VeloORM.Metadata;

/// <summary>
/// Resolves navigations across a fully-built set of entities (runs after every entity's columns are
/// known, because a navigation needs the target's table and key). Two sub-passes:
/// <list type="number">
/// <item>Reference (many-to-one): the FK is on this entity, from <c>[ForeignKey]</c> or the convention
///   <c>{NavName}Id</c>.</item>
/// <item>Collection (one-to-many): the FK is on the target — taken from <c>[ForeignKey]</c>, the
///   target's back-reference navigation, or the convention <c>{DeclaringEntity}Id</c>.</item>
/// </list>
/// A candidate whose FK column or target is missing is simply not navigable (skipped, never throws).
/// </summary>
internal static class NavigationResolver
{
    [RequiresUnreferencedCode("Reflects over entity properties to discover navigations.")]
    public static void Resolve(IReadOnlyCollection<EntityModel> entities)
    {
        var byType = entities.ToDictionary(e => e.ClrType);
        var navsByEntity = entities.ToDictionary(e => e, _ => new List<NavigationModel>());

        // Pass 1: reference navigations.
        foreach (var entity in entities)
        {
            foreach (var (property, fkOverride, underlying) in NavigationCandidates(entity))
            {
                if (TypeSupport.IsScalar(underlying) || !byType.TryGetValue(underlying, out var target))
                    continue;

                var localColumn = entity.FindColumnByProperty(fkOverride ?? property.Name + "Id");
                if (localColumn is null || target.KeyColumns.Count != 1)
                    continue;

                navsByEntity[entity].Add(new NavigationModel(
                    property, NavigationKind.Reference, target.ClrType,
                    localKeyColumnName: localColumn.ColumnName,
                    targetKeyColumnName: target.KeyColumns[0].ColumnName));
            }
            entity.Navigations = navsByEntity[entity];
        }

        // Pass 2: collection navigations (can now read the target's resolved references).
        foreach (var entity in entities)
        {
            if (entity.KeyColumns.Count != 1)
                continue;

            foreach (var (property, fkOverride, _) in NavigationCandidates(entity))
            {
                if (!TryGetElementType(property.PropertyType, out var element) || !byType.TryGetValue(element, out var target))
                    continue;

                var targetFkColumn =
                    (fkOverride is not null ? target.FindColumnByProperty(fkOverride)?.ColumnName : null)
                    ?? target.Navigations.FirstOrDefault(n => n.Kind == NavigationKind.Reference && n.TargetClrType == entity.ClrType)?.LocalKeyColumnName
                    ?? target.FindColumnByProperty(entity.ClrType.Name + "Id")?.ColumnName;
                if (targetFkColumn is null)
                    continue;

                navsByEntity[entity].Add(new NavigationModel(
                    property, NavigationKind.Collection, target.ClrType,
                    localKeyColumnName: entity.KeyColumns[0].ColumnName,
                    targetKeyColumnName: targetFkColumn));
            }
            entity.Navigations = navsByEntity[entity];
        }
    }

    [RequiresUnreferencedCode("Reflects over entity properties.")]
    private static IEnumerable<(PropertyInfo Property, string? ForeignKey, Type Underlying)> NavigationCandidates(EntityModel entity)
    {
        foreach (var property in entity.ClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;
            if (property.GetCustomAttribute<NotMappedAttribute>() is not null)
                continue;
            var fkOverride = property.GetCustomAttribute<ForeignKeyAttribute>()?.Name;
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            yield return (property, fkOverride, underlying);
        }
    }

    [RequiresUnreferencedCode("Reflects over collection interfaces to find the element type.")]
    private static bool TryGetElementType(Type type, [NotNullWhen(true)] out Type? element)
    {
        element = null;
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return false;

        foreach (var i in type.GetInterfaces().Append(type))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                element = i.GetGenericArguments()[0];
                return element.IsClass && element != typeof(string);
            }
        return false;
    }
}
