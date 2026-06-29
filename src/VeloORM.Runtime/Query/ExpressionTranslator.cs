using System.Linq.Expressions;
using System.Reflection;
using VeloORM.Metadata;
using VeloORM.Query;

namespace VeloORM.Runtime.Query;

/// <summary>
/// Translates a single-table LINQ expression tree into a <see cref="QueryModel"/>, a list of bound
/// parameter values (in placeholder order), a <see cref="ProjectionPlan"/>, and a shape key.
/// Values are always lifted into bound parameters — never inlined — so injection is impossible by
/// construction. Unsupported shapes throw <see cref="NotSupportedException"/> (never wrong SQL).
/// </summary>
internal sealed class ExpressionTranslator
{
    private readonly VeloModel _model;
    private const string RootAlias = "t0";

    private EntityModel _rootEntity = null!;
    private QueryModel _query = null!;
    private readonly List<SqlParameterBinding> _parameters = new();
    private ProjectionPlan _projection = null!;
    private bool _projected;

    public ExpressionTranslator(VeloModel model) => _model = model;

    public TranslationResult Translate(Expression expression)
    {
        VisitChain(expression);
        var key = ShapeKey.Compute(_query, _projection);
        return new TranslationResult
        {
            Model = _query,
            Parameters = _parameters,
            Terminal = _query.Terminal,
            Projection = _projection,
            Key = key,
        };
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
        _query = new QueryModel(_rootEntity.Schema, _rootEntity.TableName, RootAlias);
        foreach (var col in _rootEntity.Columns)
            _query.Select.Add(new SelectItem(new SqlColumn(RootAlias, col.ColumnName, col.ClrType), col.ColumnName));
        _projection = new ProjectionPlan
        {
            Kind = ProjectionKind.Entity,
            ResultType = _rootEntity.ClrType,
            Entity = _rootEntity,
        };
    }

    private void ApplyOperator(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                AndWhere(TranslateExpression(GetLambda(call.Arguments[1]).Body));
                break;
            case "Select":
                ApplySelect(GetLambda(call.Arguments[1]));
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
            default:
                throw new NotSupportedException(
                    $"LINQ operator '{call.Method.Name}' is not supported by the runtime engine yet.");
        }
    }

    private void ApplyOptionalPredicate(MethodCallExpression call)
    {
        if (call.Arguments.Count == 2)
            AndWhere(TranslateExpression(GetLambda(call.Arguments[1]).Body));
    }

    private void AddOrdering(MethodCallExpression call, bool descending)
    {
        var expr = TranslateExpression(GetLambda(call.Arguments[1]).Body);
        _query.OrderBy.Add(new Ordering(expr, descending));
    }

    private void AndWhere(SqlExpression predicate) =>
        _query.Where = _query.Where is null ? predicate : new SqlBinary(_query.Where, SqlBinaryOperator.And, predicate);

    // ---- projection -----------------------------------------------------

    private void ApplySelect(LambdaExpression lambda)
    {
        var body = StripConvert(lambda.Body);

        // Select(x => x): entity passthrough; nothing changes.
        if (body is ParameterExpression)
            return;

        EnsureNotAlreadyProjected();
        _query.Select.Clear();

        switch (body)
        {
            case NewExpression newExpr when newExpr.Members is { Count: > 0 }:
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
                break;
            }
            default:
            {
                // Single scalar projection: Select(x => x.Prop) or a computed scalar.
                const string alias = "c0";
                _query.Select.Add(new SelectItem(TranslateExpression(body), alias));
                _projection = new ProjectionPlan
                {
                    Kind = ProjectionKind.Scalar,
                    ResultType = body.Type,
                    ScalarAlias = alias,
                };
                break;
            }
        }

        _projected = true;
    }

    private void EnsureNotAlreadyProjected()
    {
        if (_projected)
            throw new NotSupportedException("Multiple Select projections in one query are not supported.");
    }

    // ---- expression translation ----------------------------------------

    private SqlExpression TranslateExpression(Expression expression)
    {
        expression = StripConvert(expression);

        // Any subtree that does not reference the entity parameter is a value -> bound parameter.
        if (!ReferencesRoot(expression))
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

        // Direct entity property -> column.
        if (member.Expression is ParameterExpression)
        {
            var col = _rootEntity.FindColumnByProperty(member.Member.Name)
                ?? throw new NotSupportedException(
                    $"Property '{member.Member.Name}' on '{_rootEntity.ClrType.Name}' is not a mapped column.");
            return new SqlColumn(RootAlias, col.ColumnName, col.ClrType);
        }

        throw new NotSupportedException($"Unsupported member access '{member}'.");
    }

    private SqlExpression TranslateBinary(BinaryExpression binary)
    {
        // Null comparison -> IS [NOT] NULL.
        if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            bool leftNull = !ReferencesRoot(binary.Left) && Evaluate(binary.Left) is null;
            bool rightNull = !ReferencesRoot(binary.Right) && Evaluate(binary.Right) is null;
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

        // Collection.Contains(item) / Enumerable.Contains(collection, item) -> IN (...)
        if (name == "Contains" && TryTranslateContains(call, out var inExpr))
            return inExpr!;

        // string instance methods
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
            collection = obj;            // list.Contains(x.Id)
            item = call.Arguments[0];
        }
        else if (call.Arguments.Count == 2)
        {
            collection = call.Arguments[0]; // Enumerable.Contains(list, x.Id)
            item = call.Arguments[1];
        }

        // On modern runtimes array/list.Contains binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T)
        // via an implicit array->span conversion; unwrap it back to the underlying collection.
        if (collection is not null)
            collection = UnwrapImplicitConversion(collection);

        if (collection is null || item is null || ReferencesRoot(collection) || !ReferencesRoot(item))
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

    /// <summary>Evaluates a value subexpression (constant / closure capture / member chain) without
    /// compiling where possible. Compilation is a last resort for shapes like method calls.</summary>
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
                var compiled = Expression.Lambda(expression).Compile();
                return compiled.DynamicInvoke();
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
               && Nullable.GetUnderlyingType(u.Type) == u.Operand.Type) // only strip lifting-to-nullable converts
            expression = u.Operand;
        // Also strip object/enum boxing converts that don't change semantics.
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } c
            && (c.Type == typeof(object) || c.Type.IsAssignableFrom(c.Operand.Type)))
            return c.Operand;
        return expression;
    }

    private bool ReferencesRoot(Expression expression) => RootReferenceFinder.Contains(expression, _rootEntity.ClrType);

    private static Expression UnwrapImplicitConversion(Expression e) =>
        e is MethodCallExpression { Method.Name: "op_Implicit", Object: null, Arguments.Count: 1 } mc
            ? mc.Arguments[0]
            : e;

    private static bool IsEnumerable(Type type) =>
        type != typeof(string) && type.GetInterfaces().Append(type).Any(i =>
            i == typeof(System.Collections.IEnumerable));

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

    /// <summary>Finds whether an expression references a parameter of the root entity type.</summary>
    private sealed class RootReferenceFinder : ExpressionVisitor
    {
        private readonly Type _rootType;
        private bool _found;

        private RootReferenceFinder(Type rootType) => _rootType = rootType;

        public static bool Contains(Expression expression, Type rootType)
        {
            var finder = new RootReferenceFinder(rootType);
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node.Type == _rootType)
                _found = true;
            return node;
        }
    }
}

/// <summary>The product of translating a LINQ expression for the runtime engine.</summary>
internal sealed class TranslationResult
{
    public required QueryModel Model { get; init; }
    public required List<SqlParameterBinding> Parameters { get; init; }
    public required QueryTerminal Terminal { get; init; }
    public required ProjectionPlan Projection { get; init; }
    public required string Key { get; init; }
}
