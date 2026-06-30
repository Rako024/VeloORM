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
            QueryTerminal.Sum or QueryTerminal.Average or QueryTerminal.Min or QueryTerminal.Max
                => ExecuteAggregate(statement, resultType, compiled.Terminal),
            _ => InvokeRun(compiled, statement),
        };
    }

    /// <summary>Executes a scalar aggregate and coerces the result to the LINQ-declared type, matching
    /// LINQ's empty-sequence semantics: <c>Sum</c> over no rows is 0; <c>Min/Max/Average</c> over no
    /// rows return null when the result type is nullable, otherwise throw.</summary>
    private object? ExecuteAggregate(SqlStatement statement, Type resultType, QueryTerminal terminal)
    {
        var raw = _context.Executor.ExecuteScalar<object>(statement);
        var underlying = Nullable.GetUnderlyingType(resultType);
        bool nullable = underlying is not null || !resultType.IsValueType;
        var target = underlying ?? resultType;

        if (raw is null)
        {
            if (nullable) return null;
            if (terminal == QueryTerminal.Sum) return Activator.CreateInstance(target); // 0 for numerics
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        return Convert.ChangeType(raw, target, System.Globalization.CultureInfo.InvariantCulture);
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

        if (translation.Terminal is QueryTerminal.List or QueryTerminal.First or QueryTerminal.FirstOrDefault
            or QueryTerminal.Single or QueryTerminal.SingleOrDefault or QueryTerminal.Scalar)
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

        var plans = new CollectionIncludePlan[includes.Count];
        for (int i = 0; i < includes.Count; i++)
            plans[i] = BuildCollectionPlan(includes[i]);
        return plans;
    }

    private CollectionIncludePlan BuildCollectionPlan(CollectionInclude include)
    {
        if (include.Navigation.Kind == NavigationKind.ManyToMany)
            return BuildManyToManyPlan(include);

        var dialect = _context.Dialect;
        var target = include.Target;
        // $1 binds the array of parent keys; the child FK column matches the parent key.
        var fkColumn = include.Navigation.TargetKeyColumnName;

        string sql;
        Func<DbDataReader, object> materializer;

        if (include.ChildReferences.Count == 0)
        {
            var columns = string.Join(", ", target.Columns.Select(c => dialect.QuoteIdentifier(c.ColumnName)));
            sql = $"SELECT {columns} FROM {dialect.QuoteQualifiedName(target.Schema, target.TableName)} " +
                  $"WHERE {dialect.QuoteIdentifier(fkColumn)} = ANY({dialect.RenderParameter(0)})";
            materializer = RuntimeMaterializerFactory.BuildEntityObject(target);
        }
        else
        {
            // collection→reference: the follow-up query LEFT JOINs each child reference and materializes
            // an entity graph (columns prefixed by alias so names never collide).
            const string rootAlias = "ci0";
            var select = new List<string>();
            foreach (var col in target.Columns)
                select.Add($"{dialect.QuoteIdentifier(rootAlias)}.{dialect.QuoteIdentifier(col.ColumnName)} AS {dialect.QuoteIdentifier($"{rootAlias}_{col.ColumnName}")}");

            var joins = new System.Text.StringBuilder();
            var referenceIncludes = new List<ReferenceInclude>();
            int aliasN = 1;
            foreach (var nav in include.ChildReferences)
            {
                var refTarget = _context.Model.GetEntity(nav.TargetClrType);
                var alias = "ci" + aliasN++;
                foreach (var col in refTarget.Columns)
                    select.Add($"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(col.ColumnName)} AS {dialect.QuoteIdentifier($"{alias}_{col.ColumnName}")}");
                joins.Append(" LEFT JOIN ").Append(dialect.QuoteQualifiedName(refTarget.Schema, refTarget.TableName))
                     .Append(" AS ").Append(dialect.QuoteIdentifier(alias)).Append(" ON ")
                     .Append(dialect.QuoteIdentifier(rootAlias)).Append('.').Append(dialect.QuoteIdentifier(nav.LocalKeyColumnName))
                     .Append(" = ")
                     .Append(dialect.QuoteIdentifier(alias)).Append('.').Append(dialect.QuoteIdentifier(nav.TargetKeyColumnName));
                referenceIncludes.Add(new ReferenceInclude { Navigation = nav, Target = refTarget, AliasPrefix = alias });
            }

            sql = $"SELECT {string.Join(", ", select)} FROM {dialect.QuoteQualifiedName(target.Schema, target.TableName)} AS {dialect.QuoteIdentifier(rootAlias)}{joins} " +
                  $"WHERE {dialect.QuoteIdentifier(rootAlias)}.{dialect.QuoteIdentifier(fkColumn)} = ANY({dialect.RenderParameter(0)})";

            var plan = new ProjectionPlan
            {
                Kind = ProjectionKind.EntityGraph,
                ResultType = target.ClrType,
                Entity = target,
                RootAliasPrefix = rootAlias,
                ReferenceIncludes = referenceIncludes,
            };
            materializer = RuntimeMaterializerFactory.BuildObject(plan);
        }

        return new CollectionIncludePlan
        {
            Navigation = include.Navigation,
            Target = target,
            Sql = sql,
            Materializer = materializer,
            ChildPlans = BuildCollectionPlans(include.ChildCollections),
        };
    }

    private const string ManyToManyOwnerAlias = "__velo_owner";

    /// <summary>Builds the follow-up plan for a many-to-many include: join the junction table and select
    /// each target row plus the owning parent's key (aliased <c>__velo_owner</c>) so rows can be grouped
    /// back to parents. <c>WHERE junction.localFk = ANY(parent keys)</c>.</summary>
    private CollectionIncludePlan BuildManyToManyPlan(CollectionInclude include)
    {
        var dialect = _context.Dialect;
        var target = include.Target;
        var nav = include.Navigation;
        string Q(string id) => dialect.QuoteIdentifier(id);

        var select = string.Join(", ", target.Columns.Select(c => $"{Q("t")}.{Q(c.ColumnName)}"));
        var sql =
            $"SELECT {select}, {Q("j")}.{Q(nav.JunctionLocalKeyColumn!)} AS {Q(ManyToManyOwnerAlias)} " +
            $"FROM {dialect.QuoteQualifiedName(target.Schema, target.TableName)} AS {Q("t")} " +
            $"JOIN {dialect.QuoteQualifiedName(nav.JunctionSchema, nav.JunctionTable!)} AS {Q("j")} " +
            $"ON {Q("t")}.{Q(nav.TargetKeyColumnName)} = {Q("j")}.{Q(nav.JunctionTargetKeyColumn!)} " +
            $"WHERE {Q("j")}.{Q(nav.JunctionLocalKeyColumn!)} = ANY({dialect.RenderParameter(0)})";

        var targetMaterializer = RuntimeMaterializerFactory.BuildEntityObject(target);
        Func<DbDataReader, object> materializer = r =>
        {
            var entity = targetMaterializer(r);
            var ord = r.GetOrdinal(ManyToManyOwnerAlias);
            object? owner = r.IsDBNull(ord) ? null : r.GetValue(ord);
            return new ManyToManyRow(entity, owner);
        };

        return new CollectionIncludePlan
        {
            Navigation = nav,
            Target = target,
            Sql = sql,
            Materializer = materializer,
            IsManyToMany = true,
        };
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
            if (plan.IsManyToMany)
            {
                ApplyManyToManyInclude(parents, parentEntity, plan);
                continue;
            }

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
            var allChildren = new List<object>();
            if (keys.Count > 0)
            {
                var keyArray = Array.CreateInstance(parentKeyColumn.ClrType, keys.Count);
                for (int i = 0; i < keys.Count; i++) keyArray.SetValue(keys[i], i);

                var statement = new SqlStatement(plan.Sql, [new SqlParameterBinding(keyArray, keyArray.GetType())]);
                var children = _context.Executor.Query(statement, new DelegateMaterializer<object>(plan.Materializer));

                foreach (var child in children)
                {
                    allChildren.Add(child);
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

            // Nested collection ThenIncludes: the just-loaded children become parents of a further query.
            if (plan.ChildPlans.Length > 0 && allChildren.Count > 0)
                ApplyCollectionIncludes(allChildren, plan.Target, plan.ChildPlans);
        }
    }

    /// <summary>Many-to-many follow-up: one query joins the junction, returns each target row grouped by
    /// the owning parent key (read from <c>__velo_owner</c>), and assigns lists onto the parents.</summary>
    private void ApplyManyToManyInclude(IList parents, EntityModel parentEntity, CollectionIncludePlan plan)
    {
        var nav = plan.Navigation;
        var parentKeyColumn = parentEntity.Columns.First(c => c.ColumnName == nav.LocalKeyColumnName);
        var parentKeyProperty = parentKeyColumn.Property;
        var listType = typeof(List<>).MakeGenericType(plan.Target.ClrType);

        var seen = new HashSet<object>();
        var keys = new List<object>();
        foreach (var parent in parents)
            if (parentKeyProperty.GetValue(parent) is { } k && seen.Add(k))
                keys.Add(k);

        var byOwner = new Dictionary<object, IList>();
        if (keys.Count > 0)
        {
            var keyArray = Array.CreateInstance(parentKeyColumn.ClrType, keys.Count);
            for (int i = 0; i < keys.Count; i++) keyArray.SetValue(keys[i], i);

            var statement = new SqlStatement(plan.Sql, [new SqlParameterBinding(keyArray, keyArray.GetType())]);
            var rows = _context.Executor.Query(statement, new DelegateMaterializer<object>(plan.Materializer));
            foreach (var obj in rows)
            {
                var row = (ManyToManyRow)obj;
                if (row.Owner is not { } owner) continue;
                if (!byOwner.TryGetValue(owner, out var list))
                    byOwner[owner] = list = (IList)Activator.CreateInstance(listType)!;
                list.Add(row.Entity);
            }
        }

        foreach (var parent in parents)
        {
            var key = parentKeyProperty.GetValue(parent);
            var list = key is not null && byOwner.TryGetValue(key, out var l)
                ? l
                : (IList)Activator.CreateInstance(listType)!;
            nav.Property.SetValue(parent, list);
        }
    }

    private sealed class ManyToManyRow
    {
        public ManyToManyRow(object entity, object? owner) { Entity = entity; Owner = owner; }
        public object Entity { get; }
        public object? Owner { get; }
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
        public CollectionIncludePlan[] ChildPlans { get; init; } = Array.Empty<CollectionIncludePlan>();
        public bool IsManyToMany { get; init; }
    }
}
