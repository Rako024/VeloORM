using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using VeloORM.Metadata;

namespace VeloORM.Runtime.Internal;

/// <summary>
/// Builds a compiled <c>Func&lt;DbDataReader, TElement&gt;</c> (boxed as a <see cref="Delegate"/>) for a
/// <see cref="ProjectionPlan"/>. This is the runtime fallback materializer — compiled via expression
/// trees and cached per query shape. The source-generated path (later phases) replaces it entirely.
/// </summary>
[RequiresUnreferencedCode("Builds materializers via reflection/expression trees over entity and projection types.")]
internal static class RuntimeMaterializerFactory
{
    private static readonly MethodInfo GetOrdinalMethod =
        typeof(DbDataReader).GetMethod(nameof(DbDataReader.GetOrdinal), [typeof(string)])!;

    private static readonly MethodInfo IsDbNullMethod =
        typeof(DbDataReader).GetMethod(nameof(DbDataReader.IsDBNull), [typeof(int)])!;

    private static readonly MethodInfo GetFieldValueOpenMethod =
        typeof(DbDataReader).GetMethods()
            .First(m => m.Name == nameof(DbDataReader.GetFieldValue) && m.IsGenericMethodDefinition);

    private static readonly MethodInfo SpecifyKindMethod =
        typeof(DateTime).GetMethod(nameof(DateTime.SpecifyKind), [typeof(DateTime), typeof(DateTimeKind)])!;

    public static Delegate Build(ProjectionPlan plan)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        Expression body = plan.Kind switch
        {
            ProjectionKind.Entity => BuildEntityInit(plan.Entity!, null, reader),
            ProjectionKind.EntityGraph => BuildEntityGraph(plan, reader),
            ProjectionKind.Scalar => ReadColumn(reader, plan.ScalarAlias!, plan.ResultType, restampUtc: false),
            ProjectionKind.Constructor => BuildConstructor(plan, reader),
            _ => throw new NotSupportedException($"Projection kind '{plan.Kind}' is not supported."),
        };

        var funcType = typeof(Func<,>).MakeGenericType(typeof(DbDataReader), plan.ResultType);
        return Expression.Lambda(funcType, body, reader).Compile();
    }

    /// <summary>Builds a <c>Func&lt;DbDataReader, object&gt;</c> that materializes an entity by column name —
    /// used to load collection-include children (whose element type is only known at runtime).</summary>
    public static Func<DbDataReader, object> BuildEntityObject(EntityModel entity)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        var body = Expression.Convert(BuildEntityInit(entity, null, reader), typeof(object));
        return Expression.Lambda<Func<DbDataReader, object>>(body, reader).Compile();
    }

    /// <summary>Boxed (<c>object</c>) materializer for a plan — used by collection-include follow-up
    /// queries that may themselves be entity graphs (collection→reference ThenInclude).</summary>
    public static Func<DbDataReader, object> BuildObject(ProjectionPlan plan)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        Expression body = plan.Kind switch
        {
            ProjectionKind.Entity => BuildEntityInit(plan.Entity!, null, reader),
            ProjectionKind.EntityGraph => BuildEntityGraph(plan, reader),
            _ => throw new NotSupportedException($"Projection kind '{plan.Kind}' is not a materializable entity."),
        };
        return Expression.Lambda<Func<DbDataReader, object>>(Expression.Convert(body, typeof(object)), reader).Compile();
    }

    /// <summary>Reads a single scalar from column ordinal 0 — used by the raw-SQL API.</summary>
    public static Delegate BuildScalarByOrdinal(Type resultType)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        var body = ReadValue(reader, Expression.Constant(0), resultType, restampUtc: false);
        var funcType = typeof(Func<,>).MakeGenericType(typeof(DbDataReader), resultType);
        return Expression.Lambda(funcType, body, reader).Compile();
    }

    private static Expression BuildEntityInit(EntityModel entity, string? aliasPrefix, ParameterExpression reader)
    {
        var bindings = new List<MemberBinding>(entity.Columns.Count);
        foreach (var col in entity.Columns)
            bindings.Add(Expression.Bind(col.Property, ReadColumn(reader, Alias(aliasPrefix, col.ColumnName), col.Property.PropertyType, col.NormalizeToUtc)));
        return Expression.MemberInit(Expression.New(entity.ClrType), bindings);
    }

    private static Expression BuildEntityGraph(ProjectionPlan plan, ParameterExpression reader)
    {
        var parent = plan.Entity!;
        var prefix = plan.RootAliasPrefix;
        var bindings = new List<MemberBinding>(parent.Columns.Count + (plan.ReferenceIncludes?.Count ?? 0));

        foreach (var col in parent.Columns)
            bindings.Add(Expression.Bind(col.Property, ReadColumn(reader, Alias(prefix, col.ColumnName), col.Property.PropertyType, col.NormalizeToUtc)));

        foreach (var include in plan.ReferenceIncludes!)
            bindings.Add(Expression.Bind(include.Navigation.Property, BuildIncludeValue(include, reader)));

        return Expression.MemberInit(Expression.New(parent.ClrType), bindings);
    }

    /// <summary>Materializes one reference include (and its nested ThenInclude children) from prefixed
    /// columns, yielding null when the LEFT JOIN missed (the child's key column is DBNull).</summary>
    private static Expression BuildIncludeValue(ReferenceInclude include, ParameterExpression reader)
    {
        var navType = include.Navigation.Property.PropertyType;

        var bindings = new List<MemberBinding>(include.Target.Columns.Count + include.Children.Count);
        foreach (var col in include.Target.Columns)
            bindings.Add(Expression.Bind(col.Property, ReadColumn(reader, Alias(include.AliasPrefix, col.ColumnName), col.Property.PropertyType, col.NormalizeToUtc)));
        foreach (var child in include.Children)
            bindings.Add(Expression.Bind(child.Navigation.Property, BuildIncludeValue(child, reader)));

        var childInit = Expression.MemberInit(Expression.New(include.Target.ClrType), bindings);
        var childValue = childInit.Type == navType ? (Expression)childInit : Expression.Convert(childInit, navType);

        var keyAlias = Alias(include.AliasPrefix, include.Target.KeyColumns[0].ColumnName);
        var isMissing = Expression.Call(reader, IsDbNullMethod, OrdinalOf(reader, keyAlias));
        return Expression.Condition(isMissing, Expression.Default(navType), childValue);
    }

    private static Expression BuildConstructor(ProjectionPlan plan, ParameterExpression reader)
    {
        var ctor = plan.Constructor!;
        var args = plan.ConstructorArgs!;
        var argExprs = new Expression[args.Count];
        for (int i = 0; i < args.Count; i++)
            argExprs[i] = ReadColumn(reader, args[i].Alias, args[i].Type, restampUtc: false);
        return Expression.New(ctor, argExprs);
    }

    private static string Alias(string? prefix, string column) => prefix is null ? column : $"{prefix}_{column}";

    private static Expression OrdinalOf(ParameterExpression reader, string alias) =>
        Expression.Call(reader, GetOrdinalMethod, Expression.Constant(alias));

    private static Expression ReadColumn(ParameterExpression reader, string alias, Type targetType, bool restampUtc)
    {
        var ord = Expression.Variable(typeof(int), "ord");
        var assignOrd = Expression.Assign(ord, OrdinalOf(reader, alias));
        return Expression.Block(targetType, [ord], assignOrd, ReadValue(reader, ord, targetType, restampUtc));
    }

    private static Expression ReadValue(ParameterExpression reader, Expression ord, Type targetType, bool restampUtc)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        bool isNullableValue = underlying is not null;
        bool canBeNull = isNullableValue || !targetType.IsValueType;
        var nonNullType = underlying ?? targetType;

        Expression valueExpr;
        if (nonNullType.IsEnum)
        {
            var enumUnderlying = Enum.GetUnderlyingType(nonNullType);
            var rawRead = Expression.Call(reader, GetFieldValueOpenMethod.MakeGenericMethod(enumUnderlying), ord);
            valueExpr = Expression.Convert(rawRead, targetType);
        }
        else
        {
            Expression rawRead = Expression.Call(reader, GetFieldValueOpenMethod.MakeGenericMethod(nonNullType), ord);
            // Stamp UTC-mapped DateTime columns as Kind=Utc (timestamp reads come back Unspecified).
            if (restampUtc && nonNullType == typeof(DateTime))
                rawRead = Expression.Call(SpecifyKindMethod, rawRead, Expression.Constant(DateTimeKind.Utc));
            valueExpr = nonNullType == targetType ? rawRead : Expression.Convert(rawRead, targetType);
        }

        if (!canBeNull)
            return valueExpr;

        var isNull = Expression.Call(reader, IsDbNullMethod, ord);
        return Expression.Condition(isNull, Expression.Default(targetType), valueExpr);
    }
}
