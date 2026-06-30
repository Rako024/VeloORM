using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VeloORM.Generator;

/// <summary>
/// Compile-time translator for a <c>VeloDbContext.Set&lt;T&gt;()</c>-rooted LINQ chain. It recognizes the
/// subset of operator chains whose SQL is <em>fully static</em> — i.e. carries no values that would have
/// to be captured from a closure at runtime (which a C# interceptor signature cannot receive). Supported
/// operators: <c>OrderBy/OrderByDescending/ThenBy/ThenByDescending</c> (simple column), <c>Skip/Take</c>
/// (compile-time constant), <c>Distinct</c>; terminals: <c>ToList/First/FirstOrDefault/Single/
/// SingleOrDefault</c>, zero-arg <c>Count/Any</c>, and single-selector <c>Sum/Average/Min/Max</c>.
/// Anything outside this grammar returns <c>null</c> → the call is left to the runtime engine (the
/// correctness principle). Values are never produced here, so no parameters are ever emitted.
/// </summary>
internal sealed class ChainPlan
{
    public GenEntity Entity = null!;
    public string EntityFqn = "";
    public TerminalKind Kind;
    public string Sql = "";
    /// <summary>Data terminals materialize rows; Count/Any/aggregates do not.</summary>
    public bool NeedsMaterializer;
    /// <summary>For aggregates: the fully-qualified LINQ return type (e.g. <c>decimal</c>).</summary>
    public string? ResultTypeFqn;
    /// <summary>For <c>Sum</c> over a non-nullable numeric: an empty sequence yields 0, not throw.</summary>
    public bool SumZeroIfEmpty;
}

internal static class SymbolQueryTranslator
{
    private sealed class Builder
    {
        public readonly List<(string Column, bool Descending)> OrderBy = new();
        public int? Skip;
        public int? Take;
        public bool Distinct;
    }

    /// <summary>True if the expression's method chain roots at a <c>VeloDbContext.Set&lt;T&gt;()</c> call.</summary>
    public static bool RootsAtVeloSet(ExpressionSyntax? expression, SemanticModel model, CancellationToken ct)
    {
        for (var current = expression; current is InvocationExpressionSyntax inv;
             current = inv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null)
        {
            if (model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol { Name: "Set" } s && IsVeloContext(s.ContainingType))
                return true;
        }
        return false;
    }

    public static bool IsVeloContext(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::VeloORM.Runtime.VeloDbContext")
                return true;
        return false;
    }

    /// <summary>Translates the chain terminated by <paramref name="terminal"/> to a <see cref="ChainPlan"/>,
    /// or returns <c>null</c> if any part falls outside the statically-interceptable grammar.</summary>
    public static ChainPlan? TryTranslate(InvocationExpressionSyntax terminal, SemanticModel model, CancellationToken ct)
    {
        // Peel the chain: calls[0] is the terminal, calls[^1] should be Set<T>().
        var calls = new List<InvocationExpressionSyntax>();
        for (ExpressionSyntax? e = terminal; e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma; e = ma.Expression)
            calls.Add(inv);
        if (calls.Count == 0)
            return null;

        var setCall = calls[calls.Count - 1];
        if (model.GetSymbolInfo(setCall, ct).Symbol is not IMethodSymbol { Name: "Set" } setMethod)
            return null;
        if (!IsVeloContext(setMethod.ContainingType))
            return null;
        if (setMethod.TypeArguments.Length != 1 || setMethod.TypeArguments[0] is not INamedTypeSymbol entityType)
            return null;

        var entity = SymbolModelResolver.Resolve(entityType);
        if (entity is null)
            return null;

        // Apply intermediate operators in source order: calls[^2] down to calls[1].
        var b = new Builder();
        for (int i = calls.Count - 2; i >= 1; i--)
        {
            if (!ApplyOperator(calls[i], entity, model, ct, b))
                return null;
        }

        // Resolve the terminal.
        if (model.GetSymbolInfo(terminal, ct).Symbol is not IMethodSymbol terminalSymbol)
            return null;
        var name = terminalSymbol.Name;
        int argCount = terminal.ArgumentList.Arguments.Count;

        return name switch
        {
            "ToList" when argCount == 0 => Data(entity, entityType, TerminalKind.List, b, allowPaging: true),
            "First" when argCount == 0 => Data(entity, entityType, TerminalKind.First, b, allowPaging: false),
            "FirstOrDefault" when argCount == 0 => Data(entity, entityType, TerminalKind.FirstOrDefault, b, allowPaging: false),
            "Single" when argCount == 0 => Data(entity, entityType, TerminalKind.Single, b, allowPaging: false),
            "SingleOrDefault" when argCount == 0 => Data(entity, entityType, TerminalKind.SingleOrDefault, b, allowPaging: false),
            "Count" when argCount == 0 => CountAny(entity, entityType, TerminalKind.Count, b),
            "Any" when argCount == 0 => CountAny(entity, entityType, TerminalKind.Any, b),
            "Sum" when argCount == 1 => Aggregate(entity, entityType, TerminalKind.Sum, "sum", terminal, terminalSymbol, model, ct, b),
            "Average" when argCount == 1 => Aggregate(entity, entityType, TerminalKind.Average, "avg", terminal, terminalSymbol, model, ct, b),
            "Min" when argCount == 1 => Aggregate(entity, entityType, TerminalKind.Min, "min", terminal, terminalSymbol, model, ct, b),
            "Max" when argCount == 1 => Aggregate(entity, entityType, TerminalKind.Max, "max", terminal, terminalSymbol, model, ct, b),
            _ => null, // predicate overloads, All, LongCount, ToArray, ... → runtime
        };
    }

    private static bool ApplyOperator(InvocationExpressionSyntax call, GenEntity entity, SemanticModel model, CancellationToken ct, Builder b)
    {
        if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol method)
            return false;

        switch (method.Name)
        {
            case "OrderBy":
            case "OrderByDescending":
            {
                if (ResolveColumn(call, entity) is not { } col) return false;
                b.OrderBy.Clear();
                b.OrderBy.Add((col, method.Name == "OrderByDescending"));
                return true;
            }
            case "ThenBy":
            case "ThenByDescending":
            {
                if (ResolveColumn(call, entity) is not { } col) return false;
                b.OrderBy.Add((col, method.Name == "ThenByDescending"));
                return true;
            }
            case "Skip":
            {
                if (TryConstInt(call, model, ct) is not { } n) return false;
                b.Skip = n;
                return true;
            }
            case "Take":
            {
                if (TryConstInt(call, model, ct) is not { } n) return false;
                b.Take = n;
                return true;
            }
            case "Distinct" when call.ArgumentList.Arguments.Count == 0:
                b.Distinct = true;
                return true;
            default:
                return false; // Where / Select / Include / Join / ... → not statically interceptable here
        }
    }

    /// <summary>Resolves a single-column key selector lambda (<c>x =&gt; x.Member</c>) to its column name.</summary>
    private static string? ResolveColumn(InvocationExpressionSyntax call, GenEntity entity)
    {
        if (call.ArgumentList.Arguments.Count != 1)
            return null;
        if (GetLambdaBody(call.ArgumentList.Arguments[0].Expression) is not MemberAccessExpressionSyntax member)
            return null;
        // The receiver must be the lambda parameter itself (no nested navigations).
        if (member.Expression is not IdentifierNameSyntax)
            return null;
        var propertyName = member.Name.Identifier.Text;
        return entity.Columns.FirstOrDefault(c => c.PropertyName == propertyName)?.ColumnName;
    }

    private static ExpressionSyntax? GetLambdaBody(ExpressionSyntax expr) => expr switch
    {
        SimpleLambdaExpressionSyntax { Body: ExpressionSyntax body } => body,
        ParenthesizedLambdaExpressionSyntax { Body: ExpressionSyntax body } => body,
        _ => null,
    };

    private static int? TryConstInt(InvocationExpressionSyntax call, SemanticModel model, CancellationToken ct)
    {
        if (call.ArgumentList.Arguments.Count != 1)
            return null;
        var constant = model.GetConstantValue(call.ArgumentList.Arguments[0].Expression, ct);
        return constant is { HasValue: true, Value: int n } ? n : null;
    }

    private static ChainPlan? Data(GenEntity entity, INamedTypeSymbol entityType, TerminalKind kind, Builder b, bool allowPaging)
    {
        // First/Single must not be combined with explicit paging/Distinct (subtle row-limit interplay).
        if (!allowPaging && (b.Skip is not null || b.Take is not null || b.Distinct))
            return null;

        var sql = new StringBuilder("SELECT ");
        if (b.Distinct) sql.Append("DISTINCT ");
        sql.Append(string.Join(", ", entity.Columns.Select(c => Quote(c.ColumnName))));
        sql.Append(" FROM ").Append(QuoteName(entity.Schema, entity.TableName));
        AppendOrderBy(sql, b);

        int? limit = kind is TerminalKind.First or TerminalKind.FirstOrDefault ? 1
                   : kind is TerminalKind.Single or TerminalKind.SingleOrDefault ? 2
                   : b.Take;
        if (limit is { } l) sql.Append(" LIMIT ").Append(l);
        if (b.Skip is { } s) sql.Append(" OFFSET ").Append(s);

        return new ChainPlan
        {
            Entity = entity,
            EntityFqn = Fqn(entityType),
            Kind = kind,
            Sql = sql.ToString(),
            NeedsMaterializer = true,
        };
    }

    private static ChainPlan? CountAny(GenEntity entity, INamedTypeSymbol entityType, TerminalKind kind, Builder b)
    {
        // ORDER BY is irrelevant to a count/exists and is dropped; paging/Distinct would change the result.
        if (b.Skip is not null || b.Take is not null || b.Distinct)
            return null;

        var from = "FROM " + QuoteName(entity.Schema, entity.TableName);
        var sql = kind == TerminalKind.Count ? $"SELECT count(*) {from}" : $"SELECT EXISTS(SELECT 1 {from})";
        return new ChainPlan { Entity = entity, EntityFqn = Fqn(entityType), Kind = kind, Sql = sql };
    }

    private static ChainPlan? Aggregate(
        GenEntity entity, INamedTypeSymbol entityType, TerminalKind kind, string fn,
        InvocationExpressionSyntax terminal, IMethodSymbol terminalSymbol, SemanticModel model, CancellationToken ct, Builder b)
    {
        if (b.Skip is not null || b.Take is not null || b.Distinct)
            return null;

        // The selector is the terminal's own argument (Sum(x => x.Price)).
        if (GetLambdaBody(terminal.ArgumentList.Arguments[0].Expression) is not MemberAccessExpressionSyntax member
            || member.Expression is not IdentifierNameSyntax)
            return null;
        var col = entity.Columns.FirstOrDefault(c => c.PropertyName == member.Name.Identifier.Text)?.ColumnName;
        if (col is null)
            return null;

        var returnType = terminalSymbol.ReturnType;
        bool sumZeroIfEmpty = kind == TerminalKind.Sum
            && returnType.IsValueType
            && returnType is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

        var sql = $"SELECT {fn}({Quote(col)}) FROM {QuoteName(entity.Schema, entity.TableName)}";
        return new ChainPlan
        {
            Entity = entity,
            EntityFqn = Fqn(entityType),
            Kind = kind,
            Sql = sql,
            ResultTypeFqn = Fqn(returnType),
            SumZeroIfEmpty = sumZeroIfEmpty,
        };
    }

    private static void AppendOrderBy(StringBuilder sql, Builder b)
    {
        if (b.OrderBy.Count == 0) return;
        sql.Append(" ORDER BY ");
        for (int i = 0; i < b.OrderBy.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(Quote(b.OrderBy[i].Column));
            if (b.OrderBy[i].Descending) sql.Append(" DESC");
        }
    }

    private static string Fqn(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string QuoteName(string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Quote(table) : Quote(schema!) + "." + Quote(table);

    // Identifiers are baked into a C# string literal, so the surrounding double-quotes are escaped (\").
    private static string Quote(string identifier) => "\\\"" + identifier.Replace("\"", "\"\"") + "\\\"";
}
