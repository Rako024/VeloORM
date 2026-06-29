using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace VeloORM.Generator;

/// <summary>A column resolved from a property symbol, with everything the emitter needs.</summary>
internal sealed class GenColumn
{
    public string PropertyName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    /// <summary>An expression template that reads this column from a DbDataReader named <c>r</c> at
    /// ordinal <c>{ORD}</c>, producing the property's type.</summary>
    public string ReadExpressionTemplate { get; set; } = "";
}

/// <summary>A model resolved from an entity type symbol at compile time, mirroring the runtime
/// conventions in <c>EntityModelFactory</c> (snake_case names, [Table]/[Column], scalar columns).</summary>
internal sealed class GenEntity
{
    public string TableName { get; set; } = "";
    public string? Schema { get; set; }
    public List<GenColumn> Columns { get; } = new();
}

internal static class SymbolModelResolver
{
    public static GenEntity? Resolve(INamedTypeSymbol type)
    {
        var entity = new GenEntity();

        var tableAttr = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "TableAttribute");
        entity.TableName = tableAttr?.ConstructorArguments.FirstOrDefault().Value as string
                           ?? Pluralize(ToSnakeCase(type.Name));
        entity.Schema = tableAttr?.NamedArguments.FirstOrDefault(n => n.Key == "Schema").Value.Value as string;

        foreach (var member in EnumerateProperties(type))
        {
            if (member.IsStatic || member.GetMethod is null || member.SetMethod is null)
                continue;
            if (member.IsIndexer)
                continue;
            if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "NotMappedAttribute"))
                continue;

            var (underlying, isNullableValue) = Unwrap(member.Type);
            if (!IsScalar(underlying))
                continue; // navigation / complex types are not mapped

            var columnAttr = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ColumnAttribute");
            var columnName = columnAttr?.ConstructorArguments.FirstOrDefault().Value as string
                             ?? ToSnakeCase(member.Name);

            entity.Columns.Add(new GenColumn
            {
                PropertyName = member.Name,
                ColumnName = columnName,
                ReadExpressionTemplate = BuildReadTemplate(member.Type, underlying, isNullableValue),
            });
        }

        return entity.Columns.Count > 0 ? entity : null;
    }

    private static IEnumerable<IPropertySymbol> EnumerateProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (var m in t.GetMembers().OfType<IPropertySymbol>())
                if (m.DeclaredAccessibility == Accessibility.Public && seen.Add(m.Name))
                    yield return m;
    }

    private static (ITypeSymbol Underlying, bool IsNullableValue) Unwrap(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
            return (n.TypeArguments[0], true);
        return (type, false);
    }

    private static string BuildReadTemplate(ITypeSymbol propertyType, ITypeSymbol underlying, bool isNullableValue)
    {
        const string ord = "{ORD}";
        var underlyingFqn = Fqn(underlying);

        if (underlying.TypeKind == TypeKind.Enum)
        {
            var enumUnderlying = Fqn(((INamedTypeSymbol)underlying).EnumUnderlyingType!);
            var read = $"({Fqn(underlying)})r.GetFieldValue<{enumUnderlying}>({ord})";
            return isNullableValue
                ? $"r.IsDBNull({ord}) ? ({underlyingFqn}?)null : {read}"
                : read;
        }

        if (isNullableValue)
            return $"r.IsDBNull({ord}) ? ({underlyingFqn}?)null : r.GetFieldValue<{underlyingFqn}>({ord})";

        if (underlying.IsReferenceType)
            return $"r.IsDBNull({ord}) ? null : r.GetFieldValue<{underlyingFqn}>({ord})";

        // non-null value type
        return $"r.GetFieldValue<{underlyingFqn}>({ord})";
    }

    private static string Fqn(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.None));

    private static bool IsScalar(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
            return true;

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_Char:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return true;
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
            return true;

        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name is "global::System.Guid"
            or "global::System.DateTimeOffset"
            or "global::System.TimeSpan"
            or "global::System.DateOnly"
            or "global::System.TimeOnly";
    }

    // --- naming (mirror of SnakeCaseNamingConvention) ---

    public static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                bool prevLower = i > 0 && char.IsLower(value[i - 1]);
                bool nextLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                if (i > 0 && (prevLower || nextLower)) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Pluralize(string snake)
    {
        if (snake.EndsWith("s")) return snake;
        if (snake.EndsWith("y") && snake.Length > 1 && "aeiou".IndexOf(snake[snake.Length - 2]) < 0)
            return snake.Substring(0, snake.Length - 1) + "ies";
        return snake + "s";
    }
}
