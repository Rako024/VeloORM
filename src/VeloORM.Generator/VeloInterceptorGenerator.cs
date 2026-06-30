using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VeloORM.Generator;

/// <summary>
/// Incremental generator that intercepts statically-known VeloORM query call sites
/// (<c>db.Set&lt;T&gt;().ToList()/First()/Single()/Count()/Any()</c> with no operators/predicate) and
/// bakes compile-time SQL + a reflection-free materializer via C# interceptors. Anything it is unsure
/// about is left untouched and runs through the runtime engine (the correctness principle).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VeloInterceptorGenerator : IIncrementalGenerator
{
    // Syntactic candidate terminals. Semantic validation (and whether the whole chain is statically
    // interceptable) happens in SymbolQueryTranslator.TryTranslate.
    private static readonly HashSet<string> CandidateTerminals = new()
    {
        "ToList", "First", "FirstOrDefault", "Single", "SingleOrDefault",
        "Count", "Any", "Sum", "Average", "Min", "Max",
    };

    internal static readonly DiagnosticDescriptor RuntimeFallback = new(
        id: "VELO001",
        title: "Query runs via runtime translation",
        messageFormat: "VeloORM query is not statically interceptable and will be translated at runtime",
        category: "VeloORM.Performance",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor NonStaticCompiledQuery = new(
        id: "VELO002",
        title: "Query.Compile requires a static query",
        messageFormat: "Query.Compile requires a lambda whose body is a query rooted at the context Set<T>()",
        category: "VeloORM.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCandidate(node),
            transform: static (ctx, ct) => Transform(ctx, ct))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        // Parameterized compiled queries: Query.Compile((ctx, p) => ctx.Set<T>()...).
        var compiled = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCompileCandidate(node),
            transform: static (ctx, ct) => TransformCompiled(ctx, ct))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        context.RegisterSourceOutput(candidates.Collect().Combine(compiled.Collect()),
            static (spc, pair) => Emit(spc, pair.Left, pair.Right));

        // VELO001: terminal query sites rooted at Set<T>() that we do not intercept.
        var fallbacks = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsTerminalCandidate(node),
            transform: static (ctx, ct) => DetectFallback(ctx, ct))
            .Where(static loc => loc is not null)
            .Select(static (loc, _) => loc!);
        context.RegisterSourceOutput(fallbacks, static (spc, loc) => spc.ReportDiagnostic(Diagnostic.Create(RuntimeFallback, loc)));

        // VELO002: Query.Compile calls whose argument is not a recognizable static query.
        var badCompiles = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCompileCandidate(node),
            transform: static (ctx, ct) => DetectBadCompile(ctx, ct))
            .Where(static loc => loc is not null)
            .Select(static (loc, _) => loc!);
        context.RegisterSourceOutput(badCompiles, static (spc, loc) => spc.ReportDiagnostic(Diagnostic.Create(NonStaticCompiledQuery, loc)));
    }

    private static readonly HashSet<string> TerminalNames = new()
    {
        "ToList", "ToArray", "First", "FirstOrDefault", "Single", "SingleOrDefault",
        "Count", "LongCount", "Any", "All", "Sum", "Min", "Max", "Average",
    };

    private static bool IsTerminalCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
        && TerminalNames.Contains(member.Name.Identifier.Text);

    private static Location? DetectFallback(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var member = (MemberAccessExpressionSyntax)invocation.Expression;

        if (!RootsAtVeloSet(member.Expression, ctx.SemanticModel, ct))
            return null;

        // A statically-interceptable chain is optimized — no warning. Everything else that roots at
        // Set<T>() (e.g. a Where with a captured value, Select, GroupBy, …) runs via the runtime engine.
        return SymbolQueryTranslator.TryTranslate(invocation, ctx.SemanticModel, ct) is null
            ? invocation.GetLocation()
            : null;
    }

    private static bool RootsAtVeloSet(ExpressionSyntax? expression, SemanticModel model, System.Threading.CancellationToken ct)
    {
        var current = expression;
        while (current is InvocationExpressionSyntax inv)
        {
            if (model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol { Name: "Set" } s && IsVeloContext(s.ContainingType))
                return true;
            current = inv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null;
        }
        return false;
    }

    private static bool IsCompileCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Compile" } };

    private static Location? DetectBadCompile(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return null;
        if (method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::VeloORM.Runtime.Query")
            return null;

        // The first argument must be a lambda whose body is a query rooted at the db parameter's Set<T>().
        var arg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (arg is not LambdaExpressionSyntax lambda)
            return invocation.GetLocation();

        var body = lambda.Body as ExpressionSyntax;
        if (body is null || !RootsAtVeloSet(StripTerminal(body), ctx.SemanticModel, ct))
            return invocation.GetLocation();

        return null;
    }

    private static ExpressionSyntax StripTerminal(ExpressionSyntax body)
    {
        // For "db.Set<T>()...ToList()" the body's outermost is the terminal invocation; its chain roots at Set.
        return body is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } ? ma.Expression : body;
    }

    private static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
        && CandidateTerminals.Contains(member.Name.Identifier.Text);

    private static InterceptInfo? Transform(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var model = ctx.SemanticModel;

        // Translate the whole Set<T>()-rooted chain; null means "not statically interceptable".
        var plan = SymbolQueryTranslator.TryTranslate(invocation, model, ct);
        if (plan is null)
            return null;

        var location = model.GetInterceptableLocation(invocation, ct);
        if (location is null)
            return null;

        // The source parameter type must be a CLOSED type (e.g. IQueryable<Product>), not open TSource.
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol terminal)
            return null;
        // For a reduced extension method (all LINQ operators), ReceiverType is the `this` type and
        // Parameters are the remaining arguments (e.g. an aggregate selector). Both must appear in the
        // interceptor signature for it to match. Bail if any type is open (TSource) or unnameable.
        var sourceParam = terminal.ReceiverType ?? terminal.Parameters.FirstOrDefault()?.Type;
        if (sourceParam is null || sourceParam.TypeKind == TypeKind.TypeParameter)
            return null;
        var extraParams = terminal.Parameters
            .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .ToArray();
        if (terminal.Parameters.Any(p => p.Type.TypeKind == TypeKind.TypeParameter))
            return null;

        var newExpression = plan.NeedsMaterializer ? BuildNewExpression(plan.Entity, plan.EntityFqn) : null;

        return new InterceptInfo(
            AttributeSyntax: location.GetInterceptsLocationAttributeSyntax(),
            Kind: plan.Kind,
            ReturnTypeFqn: ReturnType(plan),
            SourceParamTypeFqn: sourceParam.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EntityFqn: plan.EntityFqn,
            Sql: plan.Sql,
            NewExpression: newExpression,
            ResultTypeFqn: plan.ResultTypeFqn,
            SumZeroIfEmpty: plan.SumZeroIfEmpty,
            ExtraParamTypesOrNull: extraParams);
    }

    private static CompiledInterceptInfo? TransformCompiled(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var model = ctx.SemanticModel;

        var plan = CompiledQueryTranslator.TryTranslate(invocation, model, ct);
        if (plan is null)
            return null;

        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol compile)
            return null;
        var location = model.GetInterceptableLocation(invocation, ct);
        if (location is null)
            return null;

        // TypeArguments = [TContext, value..., TResult]. Build the Func<...> the call site sees.
        var typeArgs = compile.TypeArguments.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray();
        if (typeArgs.Length < 2 || typeArgs.Any(string.IsNullOrEmpty))
            return null;
        var funcType = "global::System.Func<" + string.Join(", ", typeArgs) + ">";
        var valueParamTypes = typeArgs.Skip(1).Take(typeArgs.Length - 2).ToArray();

        var newExpression = plan.NeedsMaterializer ? BuildNewExpression(plan.Entity, plan.EntityFqn) : null;

        return new CompiledInterceptInfo(
            AttributeSyntax: location.GetInterceptsLocationAttributeSyntax(),
            Kind: plan.Kind,
            FuncTypeFqn: funcType,
            EntityFqn: plan.EntityFqn,
            Sql: plan.Sql,
            NewExpression: newExpression,
            ValueParamTypeFqns: valueParamTypes,
            Bindings: plan.Bindings.ToArray());
    }

    private static bool IsVeloContext(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::VeloORM.Runtime.VeloDbContext")
                return true;
        return false;
    }

    private static string ReturnType(ChainPlan plan) => plan.Kind switch
    {
        TerminalKind.List => $"global::System.Collections.Generic.List<{plan.EntityFqn}>",
        TerminalKind.Count => "int",
        TerminalKind.Any => "bool",
        TerminalKind.Sum or TerminalKind.Average or TerminalKind.Min or TerminalKind.Max => plan.ResultTypeFqn!,
        _ => plan.EntityFqn, // First / Single
    };

    private static string BuildNewExpression(GenEntity entity, string entityFqn)
    {
        var sb = new StringBuilder();
        sb.Append("new ").Append(entityFqn).Append(" { ");
        for (int i = 0; i < entity.Columns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var read = entity.Columns[i].ReadExpressionTemplate.Replace("{ORD}", i.ToString());
            sb.Append(entity.Columns[i].PropertyName).Append(" = ").Append(read);
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<InterceptInfo> infos, ImmutableArray<CompiledInterceptInfo> compiled)
    {
        if (infos.IsDefaultOrEmpty && compiled.IsDefaultOrEmpty)
            return;
        if (infos.IsDefault) infos = ImmutableArray<InterceptInfo>.Empty;
        if (compiled.IsDefault) compiled = ImmutableArray<CompiledInterceptInfo>.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> VeloORM compile-time interceptors");
        sb.AppendLine("#nullable disable");
        // The interceptor attribute is file-local so it never conflicts with the BCL or other generators.
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("namespace VeloORM.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    using global::System.Data.Common;");
        sb.AppendLine("    file static class VeloGeneratedInterceptors");
        sb.AppendLine("    {");
        sb.AppendLine("        private static readonly global::VeloORM.Query.SqlParameterBinding[] NoParams = global::System.Array.Empty<global::VeloORM.Query.SqlParameterBinding>();");

        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            sb.AppendLine();
            sb.AppendLine($"        {info.AttributeSyntax}");
            // The interceptor signature must match the intercepted method exactly, so we declare (and
            // ignore) any extra arguments such as an aggregate's selector — the column is baked into SQL.
            var extra = string.Concat(info.ExtraParamTypes.Select((t, n) => $", {t} arg{n}"));
            sb.AppendLine($"        public static {info.ReturnTypeFqn} Intercept_{i}(this {info.SourceParamTypeFqn} source{extra})");

            var castSource = $"(global::System.Linq.IQueryable<{info.EntityFqn}>)source";
            var body = info.Kind switch
            {
                TerminalKind.List => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteList<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams, Materialize_{i})",
                TerminalKind.First => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteFirst<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams, Materialize_{i}, false)",
                TerminalKind.FirstOrDefault => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteFirst<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams, Materialize_{i}, true)",
                TerminalKind.Single => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteSingle<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams, Materialize_{i}, false)",
                TerminalKind.SingleOrDefault => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteSingle<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams, Materialize_{i}, true)",
                TerminalKind.Count => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteCount<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams)",
                TerminalKind.Any => $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteAny<{info.EntityFqn}>({castSource}, \"{info.Sql}\", NoParams)",
                TerminalKind.Sum or TerminalKind.Average or TerminalKind.Min or TerminalKind.Max =>
                    $"global::VeloORM.Runtime.VeloInterceptorSupport.ExecuteAggregate<{info.EntityFqn}, {info.ReturnTypeFqn}>({castSource}, \"{info.Sql}\", NoParams, {(info.SumZeroIfEmpty ? "true" : "false")})",
                _ => "default",
            };
            sb.AppendLine($"            => {body};");

            if (info.NewExpression is not null)
            {
                sb.AppendLine($"        private static {info.EntityFqn} Materialize_{i}(DbDataReader r) => {info.NewExpression};");
            }
        }

        for (int i = 0; i < compiled.Length; i++)
            EmitCompiled(sb, compiled[i], i);

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("VeloGeneratedInterceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    /// <summary>Emits a Query.Compile interceptor: a method returning a delegate that runs baked SQL and
    /// binds the lambda's typed parameters through the boxing-free sink.</summary>
    private static void EmitCompiled(StringBuilder sb, CompiledInterceptInfo info, int i)
    {
        sb.AppendLine();
        sb.AppendLine($"        {info.AttributeSyntax}");
        sb.AppendLine($"        public static {info.FuncTypeFqn} CompiledIntercept_{i}({info.FuncTypeFqn} query)");

        // Generated delegate parameters: ctx + vp0, vp1, ... (one per value parameter).
        var lambdaParams = "ctx" + string.Concat(info.ValueParamTypeFqns.Select((_, n) => $", vp{n}"));

        // Binder: add each placeholder's value in $1, $2, … order, strongly typed (no boxing).
        string binder;
        if (info.Bindings.Length == 0)
            binder = "static _ => { }";
        else
        {
            var adds = string.Concat(info.Bindings.Select(idx => $" sink.Add<{info.ValueParamTypeFqns[idx]}>(vp{idx});"));
            binder = $"sink => {{{adds} }}";
        }

        var castSource = $"(global::System.Linq.IQueryable<{info.EntityFqn}>)ctx.Set<{info.EntityFqn}>()";
        var sup = "global::VeloORM.Runtime.VeloInterceptorSupport";
        var body = info.Kind switch
        {
            TerminalKind.List => $"{sup}.ExecuteListBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", CMaterialize_{i}, {binder})",
            TerminalKind.First => $"{sup}.ExecuteFirstBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", CMaterialize_{i}, {binder}, false)",
            TerminalKind.FirstOrDefault => $"{sup}.ExecuteFirstBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", CMaterialize_{i}, {binder}, true)",
            TerminalKind.Single => $"{sup}.ExecuteSingleBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", CMaterialize_{i}, {binder}, false)",
            TerminalKind.SingleOrDefault => $"{sup}.ExecuteSingleBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", CMaterialize_{i}, {binder}, true)",
            TerminalKind.Count => $"{sup}.ExecuteCountBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", {binder})",
            TerminalKind.Any => $"{sup}.ExecuteAnyBound<{info.EntityFqn}>({castSource}, \"{info.Sql}\", {binder})",
            _ => "default",
        };
        sb.AppendLine($"            => ({lambdaParams}) => {body};");

        if (info.NewExpression is not null)
            sb.AppendLine($"        private static {info.EntityFqn} CMaterialize_{i}(DbDataReader r) => {info.NewExpression};");
    }
}

internal enum TerminalKind { List, First, FirstOrDefault, Single, SingleOrDefault, Count, Any, Sum, Average, Min, Max }

internal sealed record InterceptInfo(
    string AttributeSyntax,
    TerminalKind Kind,
    string ReturnTypeFqn,
    string SourceParamTypeFqn,
    string EntityFqn,
    string Sql,
    string? NewExpression,
    string? ResultTypeFqn = null,
    bool SumZeroIfEmpty = false,
    string[]? ExtraParamTypesOrNull = null)
{
    public string[] ExtraParamTypes => ExtraParamTypesOrNull ?? System.Array.Empty<string>();
}

internal sealed record CompiledInterceptInfo(
    string AttributeSyntax,
    TerminalKind Kind,
    string FuncTypeFqn,
    string EntityFqn,
    string Sql,
    string? NewExpression,
    string[] ValueParamTypeFqns,
    int[] Bindings);
