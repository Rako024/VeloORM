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
    private static readonly HashSet<string> Terminals =
        new() { "ToList", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Count", "Any" };

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

        context.RegisterSourceOutput(candidates.Collect(), static (spc, infos) => Emit(spc, infos));

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

        // A directly-interceptable site (Set<T>().<supported 0-arg terminal>()) is optimized — no warning.
        bool directInterceptable =
            invocation.ArgumentList.Arguments.Count == 0
            && Terminals.Contains(member.Name.Identifier.Text)
            && member.Expression is InvocationExpressionSyntax setInv
            && ctx.SemanticModel.GetSymbolInfo(setInv, ct).Symbol is IMethodSymbol { Name: "Set" } s
            && IsVeloContext(s.ContainingType);

        return directInterceptable ? null : invocation.GetLocation();
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
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax member,
            ArgumentList.Arguments.Count: 0,
        } && Terminals.Contains(member.Name.Identifier.Text);

    private static InterceptInfo? Transform(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var member = (MemberAccessExpressionSyntax)invocation.Expression;
        var model = ctx.SemanticModel;

        // The terminal must be a 0-arg LINQ operator we recognize.
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol terminal)
            return null;
        var kind = MapTerminal(terminal.Name);
        if (kind is null)
            return null;

        // The receiver must be a direct call to VeloDbContext.Set<T>() — no intervening operators.
        if (member.Expression is not InvocationExpressionSyntax setCall)
            return null;
        if (model.GetSymbolInfo(setCall, ct).Symbol is not IMethodSymbol { Name: "Set" } setMethod)
            return null;
        if (!IsVeloContext(setMethod.ContainingType))
            return null;
        if (setMethod.TypeArguments.Length != 1 || setMethod.TypeArguments[0] is not INamedTypeSymbol entityType)
            return null;

        var entity = SymbolModelResolver.Resolve(entityType);
        if (entity is null)
            return null;

        var location = model.GetInterceptableLocation(invocation, ct);
        if (location is null)
            return null;

        // The source parameter type must match the intercepted method's first parameter, as a
        // CLOSED type (e.g. IEnumerable<Product>), not the open TSource.
        var sourceParam = terminal.ReceiverType
                          ?? terminal.Parameters.FirstOrDefault()?.Type;
        if (sourceParam is null || sourceParam.TypeKind == TypeKind.TypeParameter)
            return null;

        var entityFqn = entityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var sql = BuildSql(entity, kind.Value);
        var newExpression = kind is TerminalKind.Count or TerminalKind.Any ? null : BuildNewExpression(entity, entityFqn);

        return new InterceptInfo(
            AttributeSyntax: location.GetInterceptsLocationAttributeSyntax(),
            Kind: kind.Value,
            ReturnTypeFqn: ReturnType(kind.Value, entityFqn),
            SourceParamTypeFqn: sourceParam.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EntityFqn: entityFqn,
            Sql: sql,
            NewExpression: newExpression);
    }

    private static bool IsVeloContext(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::VeloORM.Runtime.VeloDbContext")
                return true;
        return false;
    }

    private static TerminalKind? MapTerminal(string name) => name switch
    {
        "ToList" => TerminalKind.List,
        "First" => TerminalKind.First,
        "FirstOrDefault" => TerminalKind.FirstOrDefault,
        "Single" => TerminalKind.Single,
        "SingleOrDefault" => TerminalKind.SingleOrDefault,
        "Count" => TerminalKind.Count,
        "Any" => TerminalKind.Any,
        _ => null,
    };

    private static string ReturnType(TerminalKind kind, string entityFqn) => kind switch
    {
        TerminalKind.List => $"global::System.Collections.Generic.List<{entityFqn}>",
        TerminalKind.Count => "int",
        TerminalKind.Any => "bool",
        _ => entityFqn,
    };

    private static string QuoteName(string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Quote(table) : Quote(schema!) + "." + Quote(table);

    private static string Quote(string identifier) => "\\\"" + identifier.Replace("\"", "\"\"") + "\\\"";

    private static string BuildSql(GenEntity entity, TerminalKind kind)
    {
        var from = "FROM " + QuoteName(entity.Schema, entity.TableName);
        switch (kind)
        {
            case TerminalKind.Count:
                return $"SELECT count(*) {from}";
            case TerminalKind.Any:
                return $"SELECT EXISTS(SELECT 1 {from})";
            default:
                var cols = string.Join(", ", entity.Columns.Select(c => Quote(c.ColumnName)));
                var sql = $"SELECT {cols} {from}";
                if (kind is TerminalKind.First or TerminalKind.FirstOrDefault) sql += " LIMIT 1";
                if (kind is TerminalKind.Single or TerminalKind.SingleOrDefault) sql += " LIMIT 2";
                return sql;
        }
    }

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

    private static void Emit(SourceProductionContext spc, ImmutableArray<InterceptInfo> infos)
    {
        if (infos.IsDefaultOrEmpty)
            return;

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
            sb.AppendLine($"        public static {info.ReturnTypeFqn} Intercept_{i}(this {info.SourceParamTypeFqn} source)");

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
                _ => "default",
            };
            sb.AppendLine($"            => {body};");

            if (info.NewExpression is not null)
            {
                sb.AppendLine($"        private static {info.EntityFqn} Materialize_{i}(DbDataReader r) => {info.NewExpression};");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("VeloGeneratedInterceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}

internal enum TerminalKind { List, First, FirstOrDefault, Single, SingleOrDefault, Count, Any }

internal sealed record InterceptInfo(
    string AttributeSyntax,
    TerminalKind Kind,
    string ReturnTypeFqn,
    string SourceParamTypeFqn,
    string EntityFqn,
    string Sql,
    string? NewExpression);
