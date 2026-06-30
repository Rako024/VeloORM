using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace VeloORM.Metadata;

/// <summary>
/// Resolves an immutable <see cref="EntityModel"/> for a CLR type by layering, in
/// increasing precedence: naming convention defaults &lt; data annotations &lt; fluent config.
/// </summary>
public sealed class EntityModelFactory
{
    private readonly INamingConvention _naming;

    public EntityModelFactory(INamingConvention? naming = null) =>
        _naming = naming ?? SnakeCaseNamingConvention.Instance;

    public EntityModel Create(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type clrType,
        EntityConfiguration? fluent = null)
    {
        var tableName =
            fluent?.TableName
            ?? clrType.GetCustomAttribute<TableAttribute>()?.Name
            ?? _naming.TableName(clrType.Name);

        var schema =
            fluent?.Schema
            ?? clrType.GetCustomAttribute<TableAttribute>()?.Schema;

        var columns = new List<ColumnModel>();
        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue; // skip indexers
            if (!property.CanRead || !property.CanWrite)
                continue;
            if (property.GetCustomAttribute<NotMappedAttribute>() is not null)
                continue;

            var propCfg = fluent?.Properties.TryGetValue(property.Name, out var pc) == true ? pc : null;
            if (propCfg?.Ignored == true)
                continue;

            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!TypeSupport.IsScalar(underlying))
                continue; // navigation / complex types are not mapped in v1

            columns.Add(BuildColumn(property, underlying, propCfg, fluent));
        }

        ApplyKeyConvention(clrType, columns, fluent);

        var indexes = BuildIndexes(clrType, tableName, columns, fluent);

        return new EntityModel(clrType, tableName, schema, columns, indexes)
        {
            QueryFilter = fluent?.QueryFilter,
        };
    }

    private ColumnModel BuildColumn(
        PropertyInfo property,
        Type underlying,
        PropertyConfiguration? propCfg,
        EntityConfiguration? fluent)
    {
        var columnAttr = property.GetCustomAttribute<ColumnAttribute>();

        var columnName =
            propCfg?.ColumnName
            ?? columnAttr?.Name
            ?? _naming.ColumnName(property.Name);

        var storeType = propCfg?.StoreType ?? columnAttr?.TypeName;

        bool nullableByType =
            Nullable.GetUnderlyingType(property.PropertyType) is not null
            || (!property.PropertyType.IsValueType && IsNullableReference(property));
        bool isNullable = propCfg?.IsRequired is bool req ? !req : nullableByType;

        bool isKey =
            fluent?.KeyProperties?.Contains(property.Name) == true
            || property.GetCustomAttribute<KeyAttribute>() is not null;

        int keyOrder = 0;
        if (isKey)
        {
            if (fluent?.KeyProperties is { } kp && kp.Contains(property.Name))
                keyOrder = kp.IndexOf(property.Name);
            else
                keyOrder = property.GetCustomAttribute<KeyAttribute>()?.Order ?? 0;
        }

        var generated =
            propCfg?.StoreGenerated
            ?? property.GetCustomAttribute<DatabaseGeneratedAttribute>()?.Option
            ?? DefaultStoreGenerated(isKey, underlying);

        // A store-generated key cannot be null even if the CLR type is nullable.
        if (isKey)
            isNullable = false;

        return new ColumnModel(property, columnName, underlying, isNullable, isKey, keyOrder, generated, storeType);
    }

    private static StoreGenerated DefaultStoreGenerated(bool isKey, Type underlying) =>
        isKey && (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short))
            ? StoreGenerated.OnAdd
            : StoreGenerated.Never;

    private void ApplyKeyConvention(Type clrType, List<ColumnModel> columns, EntityConfiguration? fluent)
    {
        if (columns.Any(c => c.IsKey))
            return;

        // Convention: a property named "Id" or "<Type>Id" is the primary key.
        var idCandidate =
            columns.FirstOrDefault(c => string.Equals(c.PropertyName, "Id", StringComparison.Ordinal))
            ?? columns.FirstOrDefault(c =>
                string.Equals(c.PropertyName, clrType.Name + "Id", StringComparison.Ordinal));

        if (idCandidate is null)
            return;

        var index = columns.IndexOf(idCandidate);
        columns[index] = new ColumnModel(
            idCandidate.Property,
            idCandidate.ColumnName,
            idCandidate.ClrType,
            isNullable: false,
            isKey: true,
            keyOrder: 0,
            DefaultStoreGenerated(true, idCandidate.ClrType),
            idCandidate.StoreType);
    }

    private IReadOnlyList<IndexModel> BuildIndexes(
        Type clrType,
        string tableName,
        List<ColumnModel> columns,
        EntityConfiguration? fluent)
    {
        var result = new List<IndexModel>();

        foreach (var attr in clrType.GetCustomAttributes<IndexAttribute>())
            result.Add(MakeIndex(tableName, columns, attr.PropertyNames, attr.IsUnique, attr.Name));

        if (fluent is not null)
            foreach (var idx in fluent.Indexes)
                result.Add(MakeIndex(tableName, columns, idx.PropertyNames, idx.IsUnique, idx.Name));

        return result;
    }

    private static IndexModel MakeIndex(
        string tableName,
        List<ColumnModel> columns,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name)
    {
        var indexColumns = new List<ColumnModel>(propertyNames.Count);
        foreach (var propName in propertyNames)
        {
            var col = columns.FirstOrDefault(c => string.Equals(c.PropertyName, propName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Index references unknown or unmapped property '{propName}'.");
            indexColumns.Add(col);
        }

        var indexName = name ?? $"ix_{tableName}_{string.Join("_", indexColumns.Select(c => c.ColumnName))}";
        return new IndexModel(indexName, indexColumns, isUnique);
    }

    private static bool IsNullableReference(PropertyInfo property)
    {
        // Read NRT metadata: NullableAttribute(2) on the property => nullable reference.
        var nullable = property.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (nullable is { ConstructorArguments.Count: 1 })
        {
            var arg = nullable.ConstructorArguments[0];
            if (arg.ArgumentType == typeof(byte))
                return (byte)arg.Value! == 2;
            if (arg.Value is IReadOnlyList<System.Reflection.CustomAttributeTypedArgument> flags && flags.Count > 0)
                return flags[0].Value is byte b && b == 2;
        }

        // Fall back to the declaring type's NullableContext (1 = not-null, 2 = nullable).
        var context = property.DeclaringType?.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute");
        if (context is { ConstructorArguments.Count: 1 } && context.ConstructorArguments[0].Value is byte ctx)
            return ctx == 2;

        return true; // unknown context: assume nullable (safer for reads)
    }
}
