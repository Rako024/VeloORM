using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace VeloORM.Runtime.Query;

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

    public static Delegate Build(ProjectionPlan plan)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        Expression body = plan.Kind switch
        {
            ProjectionKind.Entity => BuildEntity(plan, reader),
            ProjectionKind.Scalar => ReadColumn(reader, plan.ScalarAlias!, plan.ResultType),
            ProjectionKind.Constructor => BuildConstructor(plan, reader),
            _ => throw new NotSupportedException($"Projection kind '{plan.Kind}' is not supported."),
        };

        var funcType = typeof(Func<,>).MakeGenericType(typeof(DbDataReader), plan.ResultType);
        return Expression.Lambda(funcType, body, reader).Compile();
    }

    /// <summary>Builds a materializer that reads a single scalar from column ordinal 0 — used by the
    /// raw-SQL API where output column names are not known to the model.</summary>
    public static Delegate BuildScalarByOrdinal(Type resultType)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "r");
        var body = ReadOrdinal(reader, Expression.Constant(0), resultType);
        var funcType = typeof(Func<,>).MakeGenericType(typeof(DbDataReader), resultType);
        return Expression.Lambda(funcType, body, reader).Compile();
    }

    private static Expression BuildEntity(ProjectionPlan plan, ParameterExpression reader)
    {
        var entity = plan.Entity!;
        var bindings = new List<MemberBinding>(entity.Columns.Count);
        foreach (var col in entity.Columns)
        {
            var read = ReadColumn(reader, col.ColumnName, col.Property.PropertyType);
            bindings.Add(Expression.Bind(col.Property, read));
        }
        return Expression.MemberInit(Expression.New(plan.ResultType), bindings);
    }

    private static Expression BuildConstructor(ProjectionPlan plan, ParameterExpression reader)
    {
        var ctor = plan.Constructor!;
        var args = plan.ConstructorArgs!;
        var argExprs = new Expression[args.Count];
        for (int i = 0; i < args.Count; i++)
            argExprs[i] = ReadColumn(reader, args[i].Alias, args[i].Type);
        return Expression.New(ctor, argExprs);
    }

    /// <summary>Builds <c>{ int ord = r.GetOrdinal(alias); return r.IsDBNull(ord) ? default : read(ord); }</c>
    /// with the correct null handling and enum/underlying-type conversion for <paramref name="targetType"/>.</summary>
    private static Expression ReadColumn(ParameterExpression reader, string alias, Type targetType)
    {
        var ord = Expression.Variable(typeof(int), "ord");
        var assignOrd = Expression.Assign(ord, Expression.Call(reader, GetOrdinalMethod, Expression.Constant(alias)));
        return Expression.Block(targetType, [ord], assignOrd, ReadValue(reader, ord, targetType));
    }

    /// <summary>Reads a value at a known ordinal expression (used when the ordinal is constant).</summary>
    private static Expression ReadOrdinal(ParameterExpression reader, Expression ordinal, Type targetType) =>
        ReadValue(reader, ordinal, targetType);

    private static Expression ReadValue(ParameterExpression reader, Expression ord, Type targetType)
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
            valueExpr = Expression.Convert(rawRead, targetType); // int -> enum (-> nullable enum)
        }
        else
        {
            var rawRead = Expression.Call(reader, GetFieldValueOpenMethod.MakeGenericMethod(nonNullType), ord);
            valueExpr = nonNullType == targetType ? rawRead : Expression.Convert(rawRead, targetType);
        }

        if (!canBeNull)
            return valueExpr;

        var isNull = Expression.Call(reader, IsDbNullMethod, ord);
        return Expression.Condition(isNull, Expression.Default(targetType), valueExpr);
    }
}
