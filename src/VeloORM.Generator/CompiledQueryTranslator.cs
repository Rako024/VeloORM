using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VeloORM.Generator;

/// <summary>
/// Translates a parameterized <c>Query.Compile((ctx, p0, …) =&gt; ctx.Set&lt;T&gt;()…terminal())</c> lambda
/// into baked SQL plus an ordered list of which lambda parameters feed which <c>$N</c> placeholder.
/// The compiled delegate then binds those parameters by concrete type (no boxing). Supports a
/// conservative grammar — <c>Where</c> with comparisons / <c>&amp;&amp;</c> / <c>||</c> / <c>!</c> / null
/// checks / boolean columns, <c>OrderBy/ThenBy</c>, and constant-or-parameter <c>Skip/Take</c> — and
/// returns <c>null</c> on anything else, so the call falls back to the runtime engine (the identity
/// delegate <c>Query.Compile</c> returns when not intercepted).
/// </summary>
internal sealed class CompiledPlan
{
    public GenEntity Entity = null!;
    public string EntityFqn = "";
    public TerminalKind Kind;
    public string Sql = "";
    public bool NeedsMaterializer;
    /// <summary>Ordered value-parameter indexes, one per <c>$N</c> placeholder in <c>$1, $2, …</c> order.</summary>
    public List<int> Bindings = new();
}

internal static class CompiledQueryTranslator
{
    private sealed class ChainState
    {
        public GenEntity Entity = null!;
        public string EntityFqn = "";
        public readonly List<(string Column, bool Descending)> OrderBy = new();
        public string? WhereSql;
        public string? LimitText;   // literal or "$N"
        public string? OffsetText;
        public readonly List<int> Bindings = new(); // value-param index per placeholder, in render order
        public string EntityParamName = "";          // current operator lambda's entity parameter
        public IReadOnlyDictionary<string, int> ValueParams = null!;
    }

    public static CompiledPlan? TryTranslate(InvocationExpressionSyntax compileCall, SemanticModel model, CancellationToken ct)
    {
        if (model.GetSymbolInfo(compileCall, ct).Symbol is not IMethodSymbol compile)
            return null;
        if (compile.Name != "Compile"
            || compile.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::VeloORM.Runtime.Query")
            return null;
        // TypeArgs = [TContext, value..., TResult]; value count is 0..2 (the three Compile overloads).
        var valueCount = compile.TypeArguments.Length - 2;
        if (valueCount < 0)
            return null;

        if (compileCall.ArgumentList.Arguments.Count != 1)
            return null;
        var lambda = compileCall.ArgumentList.Arguments[0].Expression;

        IReadOnlyList<ParameterSyntax> parameters;
        ExpressionSyntax body;
        switch (lambda)
        {
            case SimpleLambdaExpressionSyntax { Body: ExpressionSyntax b } sl:
                parameters = new[] { sl.Parameter };
                body = b;
                break;
            case ParenthesizedLambdaExpressionSyntax { Body: ExpressionSyntax b } pl:
                parameters = pl.ParameterList.Parameters;
                body = b;
                break;
            default:
                return null; // block-bodied or non-lambda
        }
        if (parameters.Count != valueCount + 1) // context + value params
            return null;

        var valueParams = new Dictionary<string, int>();
        for (int i = 0; i < valueCount; i++)
            valueParams[parameters[i + 1].Identifier.Text] = i;

        // Peel the chain terminated by `body`. calls[^1] must be ctx.Set<T>().
        var calls = new List<InvocationExpressionSyntax>();
        for (ExpressionSyntax? e = body; e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma; e = ma.Expression)
            calls.Add(inv);
        if (calls.Count == 0)
            return null;

        var setCall = calls[calls.Count - 1];
        if (model.GetSymbolInfo(setCall, ct).Symbol is not IMethodSymbol { Name: "Set" } setMethod
            || !SymbolQueryTranslator.IsVeloContext(setMethod.ContainingType)
            || setMethod.TypeArguments.Length != 1
            || setMethod.TypeArguments[0] is not INamedTypeSymbol entityType)
            return null;
        if (SymbolQueryTranslator.HasQueryFilter(entityType))
            return null; // defer to the runtime, which applies the model-level filter

        var entity = SymbolModelResolver.Resolve(entityType);
        if (entity is null)
            return null;

        var state = new ChainState
        {
            Entity = entity,
            EntityFqn = entityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ValueParams = valueParams,
        };

        // Apply operators in source order (calls[^2] .. calls[1]).
        for (int i = calls.Count - 2; i >= 1; i--)
        {
            if (!ApplyOperator(calls[i], model, ct, state))
                return null;
        }

        if (model.GetSymbolInfo(body, ct).Symbol is not IMethodSymbol terminal)
            return null;
        return BuildPlan(terminal.Name, body.ArgumentCountOfTerminal(), state);
    }

    private static bool ApplyOperator(InvocationExpressionSyntax call, SemanticModel model, CancellationToken ct, ChainState state)
    {
        if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol method)
            return false;

        switch (method.Name)
        {
            case "Where":
            {
                if (GetLambda(call, out var paramName, out var predicate) is false)
                    return false;
                state.EntityParamName = paramName;
                var sql = TranslatePredicate(predicate, state);
                if (sql is null)
                    return false;
                state.WhereSql = state.WhereSql is null ? sql : $"({state.WhereSql} AND {sql})";
                return true;
            }
            case "OrderBy":
            case "OrderByDescending":
            {
                if (ResolveColumn(call, state.Entity) is not { } col) return false;
                state.OrderBy.Clear();
                state.OrderBy.Add((col, method.Name == "OrderByDescending"));
                return true;
            }
            case "ThenBy":
            case "ThenByDescending":
            {
                if (ResolveColumn(call, state.Entity) is not { } col) return false;
                state.OrderBy.Add((col, method.Name == "ThenByDescending"));
                return true;
            }
            case "Skip":
                return TryPaging(call, state, isTake: false);
            case "Take":
                return TryPaging(call, state, isTake: true);
            default:
                return false;
        }
    }

    private static bool TryPaging(InvocationExpressionSyntax call, ChainState state, bool isTake)
    {
        if (call.ArgumentList.Arguments.Count != 1)
            return false;
        var arg = call.ArgumentList.Arguments[0].Expression;
        string text;
        if (arg is LiteralExpressionSyntax { Token.Value: int n })
        {
            text = n.ToString();
        }
        else if (arg is IdentifierNameSyntax id && state.ValueParams.TryGetValue(id.Identifier.Text, out var idx))
        {
            state.Bindings.Add(idx);
            text = "$" + state.Bindings.Count; // placeholder ordinal assigned in render order
        }
        else
        {
            return false;
        }

        if (isTake) state.LimitText = text; else state.OffsetText = text;
        return true;
    }

    private static CompiledPlan? BuildPlan(string terminal, int argCount, ChainState state)
    {
        TerminalKind kind;
        bool needsMaterializer;
        switch (terminal)
        {
            case "ToList" when argCount == 0: kind = TerminalKind.List; needsMaterializer = true; break;
            case "First" when argCount == 0: kind = TerminalKind.First; needsMaterializer = true; break;
            case "FirstOrDefault" when argCount == 0: kind = TerminalKind.FirstOrDefault; needsMaterializer = true; break;
            case "Single" when argCount == 0: kind = TerminalKind.Single; needsMaterializer = true; break;
            case "SingleOrDefault" when argCount == 0: kind = TerminalKind.SingleOrDefault; needsMaterializer = true; break;
            case "Count" when argCount == 0: kind = TerminalKind.Count; needsMaterializer = false; break;
            case "Any" when argCount == 0: kind = TerminalKind.Any; needsMaterializer = false; break;
            default: return null;
        }

        // Count/Any cannot meaningfully combine with paging here; bail (correctness).
        if (kind is TerminalKind.Count or TerminalKind.Any && (state.LimitText is not null || state.OffsetText is not null))
            return null;
        // First/Single must not be combined with explicit paging (subtle row-limit interplay).
        if (kind is TerminalKind.First or TerminalKind.FirstOrDefault or TerminalKind.Single or TerminalKind.SingleOrDefault
            && (state.LimitText is not null || state.OffsetText is not null))
            return null;

        var sql = new StringBuilder("SELECT ");
        if (kind == TerminalKind.Count)
        {
            sql.Append("count(*) FROM ").Append(QuoteName(state.Entity));
            AppendWhere(sql, state);
        }
        else if (kind == TerminalKind.Any)
        {
            sql.Append("EXISTS(SELECT 1 FROM ").Append(QuoteName(state.Entity));
            AppendWhere(sql, state);
            sql.Append(')');
        }
        else
        {
            sql.Append(string.Join(", ", state.Entity.Columns.Select(c => Quote(c.ColumnName))));
            sql.Append(" FROM ").Append(QuoteName(state.Entity));
            AppendWhere(sql, state);
            AppendOrderBy(sql, state);
            int? fixedLimit = kind is TerminalKind.First or TerminalKind.FirstOrDefault ? 1
                            : kind is TerminalKind.Single or TerminalKind.SingleOrDefault ? 2
                            : (int?)null;
            if (fixedLimit is { } fl) sql.Append(" LIMIT ").Append(fl);
            else if (state.LimitText is not null) sql.Append(" LIMIT ").Append(state.LimitText);
            if (state.OffsetText is not null) sql.Append(" OFFSET ").Append(state.OffsetText);
        }

        return new CompiledPlan
        {
            Entity = state.Entity,
            EntityFqn = state.EntityFqn,
            Kind = kind,
            Sql = sql.ToString(),
            NeedsMaterializer = needsMaterializer,
            Bindings = state.Bindings,
        };
    }

    private static void AppendWhere(StringBuilder sql, ChainState state)
    {
        if (state.WhereSql is not null) sql.Append(" WHERE ").Append(state.WhereSql);
    }

    private static void AppendOrderBy(StringBuilder sql, ChainState state)
    {
        if (state.OrderBy.Count == 0) return;
        sql.Append(" ORDER BY ");
        for (int i = 0; i < state.OrderBy.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(Quote(state.OrderBy[i].Column));
            if (state.OrderBy[i].Descending) sql.Append(" DESC");
        }
    }

    // ---- predicate translation ----------------------------------------

    private static string? TranslatePredicate(ExpressionSyntax expr, ChainState state)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax p:
                return TranslatePredicate(p.Expression, state);

            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not:
            {
                var inner = TranslatePredicate(not.Operand, state);
                return inner is null ? null : $"(NOT {inner})";
            }

            case BinaryExpressionSyntax bin:
                return TranslateBinary(bin, state);

            case MemberAccessExpressionSyntax member when IsColumn(member, state) is { } col:
                // A boolean column used directly as a predicate (e.g. Where(x => x.InStock)).
                return col;

            default:
                return null;
        }
    }

    private static string? TranslateBinary(BinaryExpressionSyntax bin, ChainState state)
    {
        switch (bin.Kind())
        {
            case SyntaxKind.LogicalAndExpression:
            case SyntaxKind.LogicalOrExpression:
            {
                var l = TranslatePredicate(bin.Left, state);
                var r = TranslatePredicate(bin.Right, state);
                if (l is null || r is null) return null;
                var op = bin.Kind() == SyntaxKind.LogicalAndExpression ? "AND" : "OR";
                return $"({l} {op} {r})";
            }

            case SyntaxKind.EqualsExpression:
            case SyntaxKind.NotEqualsExpression:
            case SyntaxKind.LessThanExpression:
            case SyntaxKind.LessThanOrEqualExpression:
            case SyntaxKind.GreaterThanExpression:
            case SyntaxKind.GreaterThanOrEqualExpression:
                return TranslateComparison(bin, state);

            default:
                return null;
        }
    }

    private static string? TranslateComparison(BinaryExpressionSyntax bin, ChainState state)
    {
        bool isEq = bin.Kind() == SyntaxKind.EqualsExpression;
        bool isNeq = bin.Kind() == SyntaxKind.NotEqualsExpression;

        var leftCol = IsColumn(bin.Left as MemberAccessExpressionSyntax, state);
        var rightCol = IsColumn(bin.Right as MemberAccessExpressionSyntax, state);

        // Null comparisons: column IS [NOT] NULL.
        if ((isEq || isNeq) && IsNull(bin.Right) && leftCol is not null)
            return $"({leftCol} IS {(isNeq ? "NOT " : "")}NULL)";
        if ((isEq || isNeq) && IsNull(bin.Left) && rightCol is not null)
            return $"({rightCol} IS {(isNeq ? "NOT " : "")}NULL)";

        // column OP value-param  (or value-param OP column → flip).
        if (leftCol is not null && TryValue(bin.Right, state, out var rp))
            return $"({leftCol} {Op(bin.Kind())} {rp})";
        if (rightCol is not null && TryValue(bin.Left, state, out var lp))
            return $"({rightCol} {FlipOp(bin.Kind())} {lp})";
        // column OP column.
        if (leftCol is not null && rightCol is not null)
            return $"({leftCol} {Op(bin.Kind())} {rightCol})";

        return null; // constants and anything else → bail to runtime
    }

    /// <summary>If <paramref name="expr"/> is a reference to a value parameter, records a binding and
    /// returns the placeholder; otherwise false (we deliberately do not inline constants).</summary>
    private static bool TryValue(ExpressionSyntax expr, ChainState state, out string placeholder)
    {
        placeholder = "";
        if (expr is IdentifierNameSyntax id && state.ValueParams.TryGetValue(id.Identifier.Text, out var idx))
        {
            state.Bindings.Add(idx);
            placeholder = "$" + state.Bindings.Count;
            return true;
        }
        return false;
    }

    private static string? IsColumn(MemberAccessExpressionSyntax? member, ChainState state)
    {
        if (member is null) return null;
        if (member.Expression is not IdentifierNameSyntax id || id.Identifier.Text != state.EntityParamName)
            return null;
        return state.Entity.Columns.FirstOrDefault(c => c.PropertyName == member.Name.Identifier.Text)?.ColumnName is { } col
            ? Quote(col) : null;
    }

    private static bool IsNull(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression };

    private static string Op(SyntaxKind kind) => kind switch
    {
        SyntaxKind.EqualsExpression => "=",
        SyntaxKind.NotEqualsExpression => "<>",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        _ => "=",
    };

    private static string FlipOp(SyntaxKind kind) => kind switch
    {
        SyntaxKind.LessThanExpression => ">",
        SyntaxKind.LessThanOrEqualExpression => ">=",
        SyntaxKind.GreaterThanExpression => "<",
        SyntaxKind.GreaterThanOrEqualExpression => "<=",
        _ => Op(kind), // = and <> are symmetric
    };

    private static bool GetLambda(InvocationExpressionSyntax call, out string paramName, out ExpressionSyntax body)
    {
        paramName = "";
        body = null!;
        if (call.ArgumentList.Arguments.Count != 1)
            return false;
        switch (call.ArgumentList.Arguments[0].Expression)
        {
            case SimpleLambdaExpressionSyntax { Body: ExpressionSyntax b } sl:
                paramName = sl.Parameter.Identifier.Text; body = b; return true;
            case ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1, Body: ExpressionSyntax b } pl:
                paramName = pl.ParameterList.Parameters[0].Identifier.Text; body = b; return true;
            default:
                return false;
        }
    }

    private static string? ResolveColumn(InvocationExpressionSyntax call, GenEntity entity)
    {
        if (!GetLambda(call, out _, out var body))
            return null;
        if (body is not MemberAccessExpressionSyntax member || member.Expression is not IdentifierNameSyntax)
            return null;
        return entity.Columns.FirstOrDefault(c => c.PropertyName == member.Name.Identifier.Text)?.ColumnName;
    }

    private static string QuoteName(GenEntity entity) =>
        string.IsNullOrEmpty(entity.Schema) ? Quote(entity.TableName) : Quote(entity.Schema!) + "." + Quote(entity.TableName);

    private static string Quote(string identifier) => "\\\"" + identifier.Replace("\"", "\"\"") + "\\\"";
}

internal static class SyntaxExtensions
{
    /// <summary>Argument count of the terminal invocation whose body chain we walked.</summary>
    public static int ArgumentCountOfTerminal(this ExpressionSyntax body) =>
        body is InvocationExpressionSyntax inv ? inv.ArgumentList.Arguments.Count : -1;
}
