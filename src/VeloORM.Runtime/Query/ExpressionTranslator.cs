using System.Linq.Expressions;
using System.Reflection;
using VeloORM.Metadata;
using VeloORM.Query;

namespace VeloORM.Runtime.Internal;

/// <summary>
/// Translates a LINQ expression tree into a <see cref="QueryModel"/>, bound parameters (in placeholder
/// order), a <see cref="ProjectionPlan"/>, and a shape key. Supports a single root table plus joins:
/// reference-navigation access (auto LEFT JOIN), explicit <c>Join</c>, and <c>Include</c> (reference
/// via JOIN, collection via a follow-up query). Values are always lifted into bound parameters — never
/// inlined — so injection is impossible. Unsupported shapes throw <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class ExpressionTranslator
{
    private readonly VeloModel _model;

    private EntityModel _rootEntity = null!;
    private string _rootAlias = null!;
    private QueryModel _query = null!;
    private int _aliasCounter;

    private readonly List<SqlParameterBinding> _parameters = new();
    private readonly Dictionary<ParameterExpression, (string Alias, EntityModel Entity)> _paramSources = new();
    private readonly Dictionary<(string SourceAlias, string Nav), (string Alias, EntityModel Entity)> _refJoins = new();
    private readonly List<ReferenceInclude> _referenceIncludes = new();
    private readonly List<CollectionInclude> _collectionIncludes = new();

    // What the next ThenInclude chains onto (set by Include/ThenInclude). Exactly one of RefNode /
    // CollNode is non-null; null means a ThenInclude here is unsupported and must bail.
    private (EntityModel Entity, ReferenceInclude? RefNode, CollectionInclude? CollNode)? _includeChain;

    private ProjectionPlan? _projection;

    public ExpressionTranslator(VeloModel model) => _model = model;

    private bool _ignoreFilters;

    public TranslationResult Translate(Expression expression)
    {
        VisitChain(expression);
        ApplyQueryFilter();
        BuildFinalSelect();

        var key = ShapeKey.Compute(_query, _projection!);
        if (_collectionIncludes.Count > 0)
            key += "|CI:" + string.Join(",", _collectionIncludes.Select(DescribeCollectionInclude));

        return new TranslationResult
        {
            Model = _query,
            Parameters = _parameters,
            Terminal = _query.Terminal,
            Projection = _projection!,
            RootEntity = _rootEntity,
            CollectionIncludes = _collectionIncludes,
            Key = key,
        };
    }

    private string NewAlias() => "t" + _aliasCounter++;

    /// <summary>Shape-key fragment for a collection include and its nested ThenInclude structure.</summary>
    private static string DescribeCollectionInclude(CollectionInclude c)
    {
        var refs = c.ChildReferences.Count == 0 ? "" : "[r:" + string.Join("+", c.ChildReferences.Select(n => n.PropertyName)) + "]";
        var colls = c.ChildCollections.Count == 0 ? "" : "[c:" + string.Join("+", c.ChildCollections.Select(DescribeCollectionInclude)) + "]";
        return c.Navigation.PropertyName + refs + colls;
    }

    // ---- operator chain -------------------------------------------------

    private void VisitChain(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression { Value: IQueryable root }:
                InitializeRoot(root.ElementType);
                return;
            case MethodCallExpression call:
                VisitChain(call.Arguments[0]); // process the source first
                ApplyOperator(call);
                return;
            default:
                throw new NotSupportedException($"Unsupported query root expression '{expression.NodeType}'.");
        }
    }

    private void InitializeRoot(Type entityType)
    {
        _rootEntity = _model.GetEntity(entityType);
        _rootAlias = NewAlias();
        _query = new QueryModel(_rootEntity.Schema, _rootEntity.TableName, _rootAlias);
        // SELECT items are built at the end (BuildFinalSelect), once includes/projection are known.
    }

    private void ApplyOperator(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                AndWhere(TranslateRootLambda(GetLambda(call.Arguments[1])));
                break;
            case "Select":
                MapRoot(GetLambda(call.Arguments[1]).Parameters[0]);
                BuildProjection(StripConvert(GetLambda(call.Arguments[1]).Body));
                break;
            case "OrderBy":
                _query.OrderBy.Clear();
                AddOrdering(call, descending: false);
                break;
            case "OrderByDescending":
                _query.OrderBy.Clear();
                AddOrdering(call, descending: true);
                break;
            case "ThenBy":
                AddOrdering(call, descending: false);
                break;
            case "ThenByDescending":
                AddOrdering(call, descending: true);
                break;
            case "Take":
                _query.Limit = Convert.ToInt32(Evaluate(call.Arguments[1]));
                break;
            case "Skip":
                _query.Offset = Convert.ToInt32(Evaluate(call.Arguments[1]));
                break;
            case "Distinct":
                _query.Distinct = true;
                break;
            case "IgnoreQueryFilters":
                _ignoreFilters = true;
                break;
            case "Include":
                ApplyInclude(GetLambda(call.Arguments[1]));
                break;
            case "ThenInclude":
                ApplyThenInclude(GetLambda(call.Arguments[1]));
                break;
            case "Join":
                ApplyJoin(call);
                break;
            case "First":
            case "FirstOrDefault":
                ApplyOptionalPredicate(call);
                _query.Terminal = call.Method.Name == "First" ? QueryTerminal.First : QueryTerminal.FirstOrDefault;
                break;
            case "Single":
            case "SingleOrDefault":
                ApplyOptionalPredicate(call);
                _query.Terminal = call.Method.Name == "Single" ? QueryTerminal.Single : QueryTerminal.SingleOrDefault;
                break;
            case "Any":
                ApplyOptionalPredicate(call);
                _query.Terminal = QueryTerminal.Any;
                break;
            case "Count":
            case "LongCount":
                ApplyOptionalPredicate(call);
                _query.Terminal = QueryTerminal.Count;
                break;
            case "Sum":
                ApplyAggregate(call, "sum", QueryTerminal.Sum);
                break;
            case "Average":
                ApplyAggregate(call, "avg", QueryTerminal.Average);
                break;
            case "Min":
                ApplyAggregate(call, "min", QueryTerminal.Min);
                break;
            case "Max":
                ApplyAggregate(call, "max", QueryTerminal.Max);
                break;
            default:
                throw new NotSupportedException(
                    $"LINQ operator '{call.Method.Name}' is not supported by the runtime engine yet.");
        }
    }

    private void ApplyOptionalPredicate(MethodCallExpression call)
    {
        if (call.Arguments.Count == 2)
            AndWhere(TranslateRootLambda(GetLambda(call.Arguments[1])));
    }

    /// <summary>Turns a terminal aggregate (<c>Sum/Average/Min/Max</c>) into a single
    /// <c>SELECT &lt;fn&gt;(expr)</c>. The expression comes from the selector lambda
    /// (<c>Sum(x =&gt; x.Price)</c>) or, absent one, from a prior scalar projection
    /// (<c>Select(x =&gt; x.Price).Sum()</c>).</summary>
    private void ApplyAggregate(MethodCallExpression call, string fn, QueryTerminal terminal)
    {
        SqlExpression arg;
        if (call.Arguments.Count == 2)
        {
            arg = TranslateRootLambda(GetLambda(call.Arguments[1]));
        }
        else if (_query.Select.Count == 1)
        {
            arg = _query.Select[0].Expression; // Select(x => x.Col).Sum()
        }
        else
        {
            throw new NotSupportedException(
                $"'{call.Method.Name}' requires a selector or a preceding scalar projection.");
        }

        _query.OrderBy.Clear();
        _query.Limit = null;
        _query.Offset = null;
        _query.Select.Clear();
        _query.Select.Add(new SelectItem(new SqlFunction(fn, new[] { arg }, isAggregate: true), "c0"));
        _query.Terminal = terminal;

        // The result is read via ExecuteScalar; the projection is only needed so the shape key and
        // the (unused) materializer branch see a valid plan.
        _projection = new ProjectionPlan
        {
            Kind = ProjectionKind.Scalar,
            ResultType = call.Type,
            ScalarAlias = "c0",
        };
    }

    private void AddOrdering(MethodCallExpression call, bool descending) =>
        _query.OrderBy.Add(new Ordering(TranslateRootLambda(GetLambda(call.Arguments[1])), descending));

    private void AndWhere(SqlExpression predicate) =>
        _query.Where = _query.Where is null ? predicate : new SqlBinary(_query.Where, SqlBinaryOperator.And, predicate);

    /// <summary>Applies the root entity's model-level query filter (e.g. soft delete) as an additional
    /// root WHERE, unless the chain contained <c>IgnoreQueryFilters()</c>.</summary>
    private void ApplyQueryFilter()
    {
        if (_ignoreFilters || _rootEntity.QueryFilter is not { } filter)
            return;
        AndWhere(TranslateRootLambda(filter));
    }

    private SqlExpression TranslateRootLambda(LambdaExpression lambda)
    {
        MapRoot(lambda.Parameters[0]);
        return TranslateExpression(lambda.Body);
    }

    private void MapRoot(ParameterExpression parameter) => _paramSources[parameter] = (_rootAlias, _rootEntity);

    // ---- Include --------------------------------------------------------

    private void ApplyInclude(LambdaExpression navigationSelector)
    {
        MapRoot(navigationSelector.Parameters[0]);
        if (StripConvert(navigationSelector.Body) is not MemberExpression member)
            throw new NotSupportedException("Include expects a navigation property selector.");

        var (sourceAlias, sourceEntity) = ResolveSource(member.Expression!);
        var nav = sourceEntity.FindNavigation(member.Member.Name)
            ?? throw new NotSupportedException(
                $"'{member.Member.Name}' on '{sourceEntity.ClrType.Name}' is not a navigation.");

        if (nav.Kind == NavigationKind.Reference)
        {
            var (alias, target) = EnsureReferenceJoin(sourceAlias, sourceEntity, nav);
            var node = _referenceIncludes.FirstOrDefault(r => r.AliasPrefix == alias);
            if (node is null)
            {
                node = new ReferenceInclude { Navigation = nav, Target = target, AliasPrefix = alias };
                _referenceIncludes.Add(node);
            }
            _includeChain = (target, node, null);
        }
        else
        {
            var target = _model.GetEntity(nav.TargetClrType);
            var node = _collectionIncludes.FirstOrDefault(c => c.Navigation.PropertyName == nav.PropertyName);
            if (node is null)
            {
                node = new CollectionInclude { Navigation = nav, Target = target };
                _collectionIncludes.Add(node);
            }
            _includeChain = (target, null, node);
        }
    }

    /// <summary>Extends the most recent <c>Include</c>/<c>ThenInclude</c> by one navigation hop.</summary>
    private void ApplyThenInclude(LambdaExpression navigationSelector)
    {
        if (_includeChain is not { } chain)
            throw new NotSupportedException("ThenInclude must follow an Include/ThenInclude on a navigation.");
        if (StripConvert(navigationSelector.Body) is not MemberExpression member || member.Expression is not ParameterExpression)
            throw new NotSupportedException("ThenInclude expects a direct navigation property selector.");

        var nav = chain.Entity.FindNavigation(member.Member.Name)
            ?? throw new NotSupportedException(
                $"'{member.Member.Name}' on '{chain.Entity.ClrType.Name}' is not a navigation.");

        if (chain.RefNode is { } refNode)
        {
            // Extending a JOIN-materialized reference graph in the main query.
            if (nav.Kind != NavigationKind.Reference)
                throw new NotSupportedException(
                    "ThenInclude from a reference onto a collection navigation is not supported yet.");

            var (alias, target) = EnsureReferenceJoin(refNode.AliasPrefix, refNode.Target, nav);
            var child = refNode.Children.FirstOrDefault(c => c.AliasPrefix == alias);
            if (child is null)
            {
                child = new ReferenceInclude { Navigation = nav, Target = target, AliasPrefix = alias };
                refNode.Children.Add(child);
            }
            _includeChain = (target, child, null);
        }
        else
        {
            // Extending a collection's follow-up query.
            var collNode = chain.CollNode!;
            if (nav.Kind == NavigationKind.Reference)
            {
                if (collNode.ChildReferences.All(n => n.PropertyName != nav.PropertyName))
                    collNode.ChildReferences.Add(nav);
                _includeChain = null; // a deeper hop off a collection-child reference is not supported yet
            }
            else
            {
                var target = _model.GetEntity(nav.TargetClrType);
                var child = collNode.ChildCollections.FirstOrDefault(c => c.Navigation.PropertyName == nav.PropertyName);
                if (child is null)
                {
                    child = new CollectionInclude { Navigation = nav, Target = target };
                    collNode.ChildCollections.Add(child);
                }
                _includeChain = (target, null, child);
            }
        }
    }

    // ---- Join -----------------------------------------------------------

    private void ApplyJoin(MethodCallExpression call)
    {
        if (call.Arguments[1] is not ConstantExpression { Value: IQueryable inner })
            throw new NotSupportedException("Join inner source must be a direct Set<T>().");

        var innerEntity = _model.GetEntity(inner.ElementType);
        var innerAlias = NewAlias();

        var outerKey = GetLambda(call.Arguments[2]);
        MapRoot(outerKey.Parameters[0]);
        var outerColumn = TranslateExpression(outerKey.Body);

        var innerKey = GetLambda(call.Arguments[3]);
        _paramSources[innerKey.Parameters[0]] = (innerAlias, innerEntity);
        var innerColumn = TranslateExpression(innerKey.Body);

        _query.Joins.Add(new JoinClause(
            JoinKind.Inner, innerEntity.Schema, innerEntity.TableName, innerAlias,
            new SqlBinary(outerColumn, SqlBinaryOperator.Equal, innerColumn)));

        var resultSelector = GetLambda(call.Arguments[4]);
        _paramSources[resultSelector.Parameters[0]] = (_rootAlias, _rootEntity);
        _paramSources[resultSelector.Parameters[1]] = (innerAlias, innerEntity);
        BuildProjection(StripConvert(resultSelector.Body));
    }

    // ---- projection -----------------------------------------------------

    private void BuildProjection(Expression body)
    {
        if (body is ParameterExpression)
            return; // Select(x => x): keep entity passthrough (built at the end)

        if (_projection is not null)
            throw new NotSupportedException("Multiple projections in one query are not supported.");

        _query.Select.Clear();

        if (body is NewExpression { Members.Count: > 0 } newExpr)
        {
            var args = new (string Alias, Type Type)[newExpr.Arguments.Count];
            for (int i = 0; i < newExpr.Arguments.Count; i++)
            {
                var alias = newExpr.Members[i].Name;
                _query.Select.Add(new SelectItem(TranslateExpression(newExpr.Arguments[i]), alias));
                args[i] = (alias, newExpr.Arguments[i].Type);
            }
            _projection = new ProjectionPlan
            {
                Kind = ProjectionKind.Constructor,
                ResultType = newExpr.Type,
                Constructor = newExpr.Constructor,
                ConstructorArgs = args,
            };
            return;
        }

        const string scalarAlias = "c0";
        _query.Select.Add(new SelectItem(TranslateExpression(body), scalarAlias));
        _projection = new ProjectionPlan
        {
            Kind = ProjectionKind.Scalar,
            ResultType = body.Type,
            ScalarAlias = scalarAlias,
        };
    }

    /// <summary>Builds the SELECT list for the default (non-projected) cases at the end of translation.</summary>
    private void BuildFinalSelect()
    {
        if (_projection is not null)
            return; // an explicit Select / Join already defined the projection + SELECT

        if (_referenceIncludes.Count == 0)
        {
            foreach (var col in _rootEntity.Columns)
                _query.Select.Add(new SelectItem(new SqlColumn(_rootAlias, col.ColumnName, col.ClrType), col.ColumnName));
            _projection = new ProjectionPlan { Kind = ProjectionKind.Entity, ResultType = _rootEntity.ClrType, Entity = _rootEntity };
            return;
        }

        // Entity graph: prefix every column with its source alias so names never collide.
        foreach (var col in _rootEntity.Columns)
            _query.Select.Add(new SelectItem(new SqlColumn(_rootAlias, col.ColumnName, col.ClrType), $"{_rootAlias}_{col.ColumnName}"));
        foreach (var include in _referenceIncludes)
            EmitIncludeColumns(include);

        _projection = new ProjectionPlan
        {
            Kind = ProjectionKind.EntityGraph,
            ResultType = _rootEntity.ClrType,
            Entity = _rootEntity,
            RootAliasPrefix = _rootAlias,
            ReferenceIncludes = _referenceIncludes,
        };
    }

    /// <summary>Emits prefixed SELECT columns for a reference include and (recursively) its nested
    /// ThenInclude children.</summary>
    private void EmitIncludeColumns(ReferenceInclude include)
    {
        foreach (var col in include.Target.Columns)
            _query.Select.Add(new SelectItem(new SqlColumn(include.AliasPrefix, col.ColumnName, col.ClrType), $"{include.AliasPrefix}_{col.ColumnName}"));
        foreach (var child in include.Children)
            EmitIncludeColumns(child);
    }

    // ---- source / navigation resolution --------------------------------

    private (string Alias, EntityModel Entity) ResolveSource(Expression expression)
    {
        expression = StripConvert(expression);
        switch (expression)
        {
            case ParameterExpression p when _paramSources.TryGetValue(p, out var src):
                return src;
            case MemberExpression m:
            {
                var (alias, entity) = ResolveSource(m.Expression!);
                var nav = entity.FindNavigation(m.Member.Name);
                if (nav is { Kind: NavigationKind.Reference })
                    return EnsureReferenceJoin(alias, entity, nav);
                throw new NotSupportedException($"'{m.Member.Name}' is not a reference navigation on '{entity.ClrType.Name}'.");
            }
            default:
                throw new NotSupportedException($"Cannot resolve query source from '{expression.NodeType}'.");
        }
    }

    private (string Alias, EntityModel Entity) EnsureReferenceJoin(string sourceAlias, EntityModel sourceEntity, NavigationModel nav)
    {
        var cacheKey = (sourceAlias, nav.PropertyName);
        if (_refJoins.TryGetValue(cacheKey, out var existing))
            return existing;

        var target = _model.GetEntity(nav.TargetClrType);
        var alias = NewAlias();
        var on = new SqlBinary(
            new SqlColumn(sourceAlias, nav.LocalKeyColumnName, typeof(object)),
            SqlBinaryOperator.Equal,
            new SqlColumn(alias, nav.TargetKeyColumnName, typeof(object)));
        _query.Joins.Add(new JoinClause(JoinKind.Left, target.Schema, target.TableName, alias, on));

        var result = (alias, target);
        _refJoins[cacheKey] = result;
        return result;
    }

    // ---- expression translation ----------------------------------------

    private SqlExpression TranslateExpression(Expression expression)
    {
        expression = StripConvert(expression);

        if (!ReferencesScope(expression))
            return Parameter(Evaluate(expression), expression.Type);

        return expression switch
        {
            MemberExpression member => TranslateMember(member),
            BinaryExpression binary => TranslateBinary(binary),
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conv => TranslateExpression(conv.Operand),
            UnaryExpression { NodeType: ExpressionType.Not } unary => new SqlUnary(SqlUnaryOperator.Not, TranslateExpression(unary.Operand)),
            UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } unary => new SqlUnary(SqlUnaryOperator.Negate, TranslateExpression(unary.Operand)),
            MethodCallExpression methodCall => TranslateMethodCall(methodCall),
            _ => throw new NotSupportedException($"Unsupported expression node '{expression.NodeType}'."),
        };
    }

    private SqlExpression TranslateMember(MemberExpression member)
    {
        // Nullable<T>.HasValue / .Value
        if (member.Member.Name == "HasValue" && member.Expression is { } hv && Nullable.GetUnderlyingType(hv.Type) is not null)
            return new SqlIsNull(TranslateExpression(hv), negated: true);
        if (member.Member.Name == "Value" && member.Expression is { } v && Nullable.GetUnderlyingType(v.Type) is not null)
            return TranslateExpression(v);

        // string.Length -> char_length(col)
        if (member.Member.Name == "Length" && member.Expression?.Type == typeof(string))
            return new SqlFunction("char_length", [TranslateExpression(member.Expression)]);

        // Resolve the owning source (root parameter, joined parameter, or a reference-navigation chain).
        var (alias, entity) = ResolveSource(member.Expression!);
        var col = entity.FindColumnByProperty(member.Member.Name)
            ?? throw new NotSupportedException(
                $"Property '{member.Member.Name}' on '{entity.ClrType.Name}' is not a mapped column.");
        return new SqlColumn(alias, col.ColumnName, col.ClrType);
    }

    private SqlExpression TranslateBinary(BinaryExpression binary)
    {
        if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            bool leftNull = !ReferencesScope(binary.Left) && Evaluate(binary.Left) is null;
            bool rightNull = !ReferencesScope(binary.Right) && Evaluate(binary.Right) is null;
            if (leftNull ^ rightNull)
            {
                var operand = leftNull ? binary.Right : binary.Left;
                return new SqlIsNull(TranslateExpression(operand), negated: binary.NodeType == ExpressionType.NotEqual);
            }
        }

        var op = MapBinaryOperator(binary.NodeType, binary.Type);
        return new SqlBinary(TranslateExpression(binary.Left), op, TranslateExpression(binary.Right));
    }

    private SqlExpression TranslateMethodCall(MethodCallExpression call)
    {
        var name = call.Method.Name;

        if (name == "Contains" && TryTranslateContains(call, out var inExpr))
            return inExpr!;

        if (call.Object is { } target && target.Type == typeof(string))
        {
            var operand = TranslateExpression(target);
            switch (name)
            {
                case "StartsWith": return new SqlLike(operand, Parameter(EscapeLike(EvaluateString(call.Arguments[0])) + "%", typeof(string)));
                case "EndsWith": return new SqlLike(operand, Parameter("%" + EscapeLike(EvaluateString(call.Arguments[0])), typeof(string)));
                case "Contains": return new SqlLike(operand, Parameter("%" + EscapeLike(EvaluateString(call.Arguments[0])) + "%", typeof(string)));
                case "ToLower": return new SqlFunction("lower", [operand]);
                case "ToUpper": return new SqlFunction("upper", [operand]);
                case "Trim": return new SqlFunction("btrim", [operand]);
            }
        }

        throw new NotSupportedException($"Method '{call.Method.DeclaringType?.Name}.{name}' is not translatable.");
    }

    private bool TryTranslateContains(MethodCallExpression call, out SqlExpression? result)
    {
        result = null;
        Expression? collection = null;
        Expression? item = null;

        if (call.Object is { } obj && obj.Type != typeof(string) && IsEnumerable(obj.Type))
        {
            collection = obj;
            item = call.Arguments[0];
        }
        else if (call.Arguments.Count == 2)
        {
            collection = call.Arguments[0];
            item = call.Arguments[1];
        }

        if (collection is not null)
            collection = UnwrapImplicitConversion(collection);

        if (collection is null || item is null || ReferencesScope(collection) || !ReferencesScope(item))
            return false;

        var values = new List<SqlExpression>();
        var elementType = GetEnumerableElementType(collection.Type);
        foreach (var element in (System.Collections.IEnumerable)(Evaluate(collection) ?? Array.Empty<object>()))
            values.Add(Parameter(element, elementType));

        result = new SqlIn(TranslateExpression(item), values);
        return true;
    }

    // ---- value handling -------------------------------------------------

    private SqlParameter Parameter(object? value, Type clrType)
    {
        var parameter = new SqlParameter(value, clrType) { Ordinal = _parameters.Count };
        _parameters.Add(new SqlParameterBinding(value, clrType));
        return parameter;
    }

    private static object? Evaluate(Expression expression)
    {
        expression = StripConvert(expression);
        switch (expression)
        {
            case ConstantExpression c:
                return c.Value;
            case MemberExpression m:
                var instance = m.Expression is null ? null : Evaluate(m.Expression);
                return GetMemberValue(m.Member, instance);
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } na:
                var array = Array.CreateInstance(na.Type.GetElementType()!, na.Expressions.Count);
                for (int i = 0; i < na.Expressions.Count; i++)
                    array.SetValue(Evaluate(na.Expressions[i]), i);
                return array;
            default:
                return Expression.Lambda(expression).Compile().DynamicInvoke();
        }
    }

    private static string EvaluateString(Expression expression) =>
        Evaluate(expression) as string ?? throw new NotSupportedException("Expected a constant string argument.");

    private static object? GetMemberValue(MemberInfo member, object? instance) => member switch
    {
        FieldInfo f => f.GetValue(instance),
        PropertyInfo p => p.GetValue(instance),
        _ => throw new NotSupportedException($"Cannot read member '{member.Name}'."),
    };

    // ---- helpers --------------------------------------------------------

    private static LambdaExpression GetLambda(Expression expression) => expression switch
    {
        UnaryExpression { Operand: LambdaExpression l } => l,
        LambdaExpression l => l,
        _ => throw new NotSupportedException($"Expected a lambda but found '{expression.NodeType}'."),
    };

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
               && Nullable.GetUnderlyingType(u.Type) == u.Operand.Type)
            expression = u.Operand;
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } c
            && (c.Type == typeof(object) || c.Type.IsAssignableFrom(c.Operand.Type)))
            return c.Operand;
        return expression;
    }

    private bool ReferencesScope(Expression expression) => ScopeReferenceFinder.Contains(expression, _paramSources.Keys);

    private static Expression UnwrapImplicitConversion(Expression e) =>
        e is MethodCallExpression { Method.Name: "op_Implicit", Object: null, Arguments.Count: 1 } mc
            ? mc.Arguments[0]
            : e;

    private static bool IsEnumerable(Type type) =>
        type != typeof(string) && type.GetInterfaces().Append(type).Any(i => i == typeof(System.Collections.IEnumerable));

    private static Type GetEnumerableElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType()!;
        foreach (var i in type.GetInterfaces().Append(type))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return typeof(object);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static SqlBinaryOperator MapBinaryOperator(ExpressionType nodeType, Type resultType) => nodeType switch
    {
        ExpressionType.AndAlso or ExpressionType.And => SqlBinaryOperator.And,
        ExpressionType.OrElse or ExpressionType.Or => SqlBinaryOperator.Or,
        ExpressionType.Equal => SqlBinaryOperator.Equal,
        ExpressionType.NotEqual => SqlBinaryOperator.NotEqual,
        ExpressionType.GreaterThan => SqlBinaryOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
        ExpressionType.LessThan => SqlBinaryOperator.LessThan,
        ExpressionType.LessThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
        ExpressionType.Add when resultType == typeof(string) => SqlBinaryOperator.Concat,
        ExpressionType.Add or ExpressionType.AddChecked => SqlBinaryOperator.Add,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => SqlBinaryOperator.Subtract,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => SqlBinaryOperator.Multiply,
        ExpressionType.Divide => SqlBinaryOperator.Divide,
        ExpressionType.Modulo => SqlBinaryOperator.Modulo,
        _ => throw new NotSupportedException($"Binary operator '{nodeType}' is not supported."),
    };

    /// <summary>Finds whether an expression references any in-scope query parameter.</summary>
    private sealed class ScopeReferenceFinder : ExpressionVisitor
    {
        private readonly IReadOnlyCollection<ParameterExpression> _scope;
        private bool _found;

        private ScopeReferenceFinder(IReadOnlyCollection<ParameterExpression> scope) => _scope = scope;

        public static bool Contains(Expression expression, IReadOnlyCollection<ParameterExpression> scope)
        {
            var finder = new ScopeReferenceFinder(scope);
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (_scope.Contains(node))
                _found = true;
            return node;
        }
    }
}

/// <summary>A collection navigation to load by a follow-up query and stitch by foreign key.
/// <see cref="ChildReferences"/> are reference navigations on the child entity to LEFT JOIN into the
/// follow-up query (collection→reference); <see cref="ChildCollections"/> are nested collection
/// navigations loaded by further follow-up queries (collection→collection).</summary>
internal sealed class CollectionInclude
{
    public required NavigationModel Navigation { get; init; }
    public required EntityModel Target { get; init; }
    public List<NavigationModel> ChildReferences { get; } = new();
    public List<CollectionInclude> ChildCollections { get; } = new();
}

/// <summary>The product of translating a LINQ expression for the runtime engine.</summary>
internal sealed class TranslationResult
{
    public required QueryModel Model { get; init; }
    public required List<SqlParameterBinding> Parameters { get; init; }
    public required QueryTerminal Terminal { get; init; }
    public required ProjectionPlan Projection { get; init; }
    public required EntityModel RootEntity { get; init; }
    public required IReadOnlyList<CollectionInclude> CollectionIncludes { get; init; }
    public required string Key { get; init; }
}
