using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using VeloORM.Query;
using VeloORM.Runtime.Materialization;

namespace VeloORM.Runtime.Internal;

/// <summary>
/// Orchestrates the runtime execution path: translate the expression, look up (or compile) the SQL +
/// materializer keyed by query shape, then execute. Compilation (SQL rendering + materializer build)
/// happens once per shape; re-executing the same shape with different values reuses the cached entry
/// — see <see cref="CompilationCount"/>.
/// </summary>
internal sealed class QueryEngine
{
    private static readonly MethodInfo RunOpenMethod =
        typeof(QueryEngine).GetMethod(nameof(Run), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly VeloDbContext _context;
    private readonly ConcurrentDictionary<string, CompiledQuery> _cache = new();
    private long _compilations;

    public QueryEngine(VeloDbContext context) => _context = context;

    public long CompilationCount => Interlocked.Read(ref _compilations);

    public object? Execute(Expression expression, Type resultType)
    {
        var translation = new ExpressionTranslator(_context.Model).Translate(expression);
        var compiled = _cache.GetOrAdd(translation.Key, _ => Compile(translation));
        var statement = new SqlStatement(compiled.Sql, translation.Parameters);

        return compiled.Terminal switch
        {
            QueryTerminal.Count => ConvertCount(_context.Executor.ExecuteScalar<long>(statement), resultType),
            QueryTerminal.Any => _context.Executor.ExecuteScalar<bool>(statement),
            _ => InvokeRun(compiled, statement),
        };
    }

    private object? InvokeRun(CompiledQuery compiled, SqlStatement statement)
    {
        try
        {
            return compiled.RunMethod!.Invoke(this, [compiled, statement]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real exception (e.g. "Sequence contains more than one element") rather
            // than the reflection wrapper, preserving its stack trace.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }

    private CompiledQuery Compile(TranslationResult translation)
    {
        Interlocked.Increment(ref _compilations);

        // Parameters carry pre-assigned ordinals, so the builder renders placeholders without
        // collecting values; the binding list comes from the translator.
        var statement = SqlBuilder.Build(translation.Model, _context.Dialect);

        var compiled = new CompiledQuery
        {
            Sql = statement.Sql,
            Terminal = translation.Terminal,
        };

        if (translation.Terminal is not (QueryTerminal.Count or QueryTerminal.Any))
        {
            compiled.Materializer = RuntimeMaterializerFactory.Build(translation.Projection);
            compiled.ResultElementType = translation.Projection.ResultType;
            compiled.RunMethod = RunOpenMethod.MakeGenericMethod(translation.Projection.ResultType);
        }

        return compiled;
    }

    // Invoked via reflection (RunMethod) with the closed element type.
    private object? Run<TElement>(CompiledQuery compiled, SqlStatement statement)
    {
        var materialize = (Func<DbDataReader, TElement>)compiled.Materializer!;
        var rows = _context.Executor.Query(statement, new DelegateMaterializer<TElement>(materialize));

        return compiled.Terminal switch
        {
            QueryTerminal.First => rows.Count > 0 ? rows[0]! : throw NoElements(),
            QueryTerminal.FirstOrDefault => rows.Count > 0 ? rows[0] : default,
            QueryTerminal.Single => SingleResult(rows, orDefault: false),
            QueryTerminal.SingleOrDefault => SingleResult(rows, orDefault: true),
            _ => rows, // List
        };
    }

    private static object? SingleResult<TElement>(List<TElement> rows, bool orDefault)
    {
        if (rows.Count > 1)
            throw new InvalidOperationException("Sequence contains more than one element.");
        if (rows.Count == 0)
            return orDefault ? default(TElement) : throw NoElements();
        return rows[0];
    }

    private static object ConvertCount(long count, Type resultType)
    {
        if (resultType == typeof(long)) return count;
        if (resultType == typeof(int)) return checked((int)count);
        return Convert.ChangeType(count, resultType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static InvalidOperationException NoElements() => new("Sequence contains no elements.");

    private sealed class CompiledQuery
    {
        public required string Sql { get; init; }
        public required QueryTerminal Terminal { get; init; }
        public Delegate? Materializer { get; set; }
        public Type? ResultElementType { get; set; }
        public MethodInfo? RunMethod { get; set; }
    }
}
