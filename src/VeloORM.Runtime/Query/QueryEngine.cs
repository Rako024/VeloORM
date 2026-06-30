using System.Collections;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using VeloORM.Metadata;
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
            compiled.ParentEntity = translation.RootEntity;
            compiled.CollectionPlans = BuildCollectionPlans(translation.CollectionIncludes);
        }

        return compiled;
    }

    private CollectionIncludePlan[] BuildCollectionPlans(IReadOnlyList<CollectionInclude> includes)
    {
        if (includes.Count == 0)
            return Array.Empty<CollectionIncludePlan>();

        var dialect = _context.Dialect;
        var plans = new CollectionIncludePlan[includes.Count];
        for (int i = 0; i < includes.Count; i++)
        {
            var target = includes[i].Target;
            var columns = string.Join(", ", target.Columns.Select(c => dialect.QuoteIdentifier(c.ColumnName)));
            // SELECT <cols> FROM <target> WHERE <fk> = ANY($1)  — $1 binds the array of parent keys.
            var sql = $"SELECT {columns} FROM {dialect.QuoteQualifiedName(target.Schema, target.TableName)} " +
                      $"WHERE {dialect.QuoteIdentifier(includes[i].Navigation.TargetKeyColumnName)} = ANY({dialect.RenderParameter(0)})";
            plans[i] = new CollectionIncludePlan
            {
                Navigation = includes[i].Navigation,
                Target = target,
                Sql = sql,
                Materializer = RuntimeMaterializerFactory.BuildEntityObject(target),
            };
        }
        return plans;
    }

    // Invoked via reflection (RunMethod) with the closed element type.
    private object? Run<TElement>(CompiledQuery compiled, SqlStatement statement)
    {
        var materialize = (Func<DbDataReader, TElement>)compiled.Materializer!;
        var rows = _context.Executor.Query(statement, new DelegateMaterializer<TElement>(materialize));

        if (compiled.CollectionPlans.Length > 0 && rows.Count > 0)
            ApplyCollectionIncludes(rows, compiled.ParentEntity!, compiled.CollectionPlans);

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

    /// <summary>Loads each collection navigation with one follow-up query (WHERE fk = ANY(parent keys))
    /// and stitches the children onto the materialized parents by foreign key.</summary>
    private void ApplyCollectionIncludes(IList parents, EntityModel parentEntity, CollectionIncludePlan[] plans)
    {
        foreach (var plan in plans)
        {
            var nav = plan.Navigation;
            var parentKeyColumn = parentEntity.Columns.First(c => c.ColumnName == nav.LocalKeyColumnName);
            var parentKeyProperty = parentKeyColumn.Property;
            var childFkProperty = plan.Target.Columns.First(c => c.ColumnName == nav.TargetKeyColumnName).Property;
            var listType = typeof(List<>).MakeGenericType(plan.Target.ClrType);

            // Distinct, non-null parent keys.
            var seen = new HashSet<object>();
            var keys = new List<object>();
            foreach (var parent in parents)
                if (parentKeyProperty.GetValue(parent) is { } k && seen.Add(k))
                    keys.Add(k);

            var byForeignKey = new Dictionary<object, IList>();
            if (keys.Count > 0)
            {
                var keyArray = Array.CreateInstance(parentKeyColumn.ClrType, keys.Count);
                for (int i = 0; i < keys.Count; i++) keyArray.SetValue(keys[i], i);

                var statement = new SqlStatement(plan.Sql, [new SqlParameterBinding(keyArray, keyArray.GetType())]);
                var children = _context.Executor.Query(statement, new DelegateMaterializer<object>(plan.Materializer));

                foreach (var child in children)
                {
                    if (childFkProperty.GetValue(child) is not { } fk) continue;
                    if (!byForeignKey.TryGetValue(fk, out var list))
                        byForeignKey[fk] = list = (IList)Activator.CreateInstance(listType)!;
                    list.Add(child);
                }
            }

            foreach (var parent in parents)
            {
                var key = parentKeyProperty.GetValue(parent);
                var list = key is not null && byForeignKey.TryGetValue(key, out var l)
                    ? l
                    : (IList)Activator.CreateInstance(listType)!;
                nav.Property.SetValue(parent, list);
            }
        }
    }

    private sealed class CompiledQuery
    {
        public required string Sql { get; init; }
        public required QueryTerminal Terminal { get; init; }
        public Delegate? Materializer { get; set; }
        public Type? ResultElementType { get; set; }
        public MethodInfo? RunMethod { get; set; }
        public EntityModel? ParentEntity { get; set; }
        public CollectionIncludePlan[] CollectionPlans { get; set; } = Array.Empty<CollectionIncludePlan>();
    }

    private sealed class CollectionIncludePlan
    {
        public required NavigationModel Navigation { get; init; }
        public required EntityModel Target { get; init; }
        public required string Sql { get; init; }
        public required Func<DbDataReader, object> Materializer { get; init; }
    }
}
