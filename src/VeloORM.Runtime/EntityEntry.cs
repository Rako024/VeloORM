using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using VeloORM.Materialization;
using VeloORM.Metadata;
using VeloORM.Query;
using VeloORM.Runtime.Internal;
using VeloORM.Runtime.Materialization;

namespace VeloORM.Runtime;

public partial class VeloDbContext
{
    /// <summary>Returns a stateless handle for explicitly loading a navigation of an already-materialized
    /// entity (e.g. <c>db.Entry(order).Reference(o =&gt; o.User).Load()</c>). VeloORM does not track
    /// entities — this simply runs a targeted query and assigns the result onto the given instance.</summary>
    public EntityEntry<T> Entry<T>(T entity) where T : class => new(this, entity, Model.GetEntity(typeof(T)));
}

/// <summary>Explicit-loading handle for one entity instance. No change tracking is involved.</summary>
public sealed class EntityEntry<T> where T : class
{
    private readonly VeloDbContext _context;
    private readonly T _entity;
    private readonly EntityModel _model;

    internal EntityEntry(VeloDbContext context, T entity, EntityModel model)
    {
        _context = context;
        _entity = entity;
        _model = model;
    }

    public NavigationEntry<T> Reference<TProperty>(Expression<Func<T, TProperty>> navigation) =>
        For(navigation, NavigationKind.Reference);

    public NavigationEntry<T> Collection<TProperty>(Expression<Func<T, TProperty>> navigation) =>
        For(navigation, NavigationKind.Collection);

    private NavigationEntry<T> For<TProperty>(Expression<Func<T, TProperty>> navigation, NavigationKind expected)
    {
        var name = MemberName(navigation);
        var nav = _model.FindNavigation(name)
            ?? throw new InvalidOperationException($"'{name}' is not a navigation on '{_model.ClrType.Name}'.");
        if (nav.Kind != expected)
            throw new InvalidOperationException($"'{name}' is a {nav.Kind} navigation, not {expected}.");
        return new NavigationEntry<T>(_context, _entity, _model, nav);
    }

    private static string MemberName<TProperty>(Expression<Func<T, TProperty>> e)
    {
        var body = e.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : e.Body;
        return body is MemberExpression m ? m.Member.Name
            : throw new ArgumentException("Navigation selector must be a simple property access.");
    }
}

/// <summary>A single loadable navigation of an entity instance.</summary>
public sealed class NavigationEntry<T> where T : class
{
    private readonly VeloDbContext _context;
    private readonly T _entity;
    private readonly EntityModel _model;
    private readonly NavigationModel _nav;

    internal NavigationEntry(VeloDbContext context, T entity, EntityModel model, NavigationModel nav)
    {
        _context = context;
        _entity = entity;
        _model = model;
        _nav = nav;
    }

    /// <summary>Loads the related data and assigns it onto the entity (reference → single instance,
    /// collection → a populated list). A null local key yields a null reference / empty collection.</summary>
    [RequiresUnreferencedCode("Reads/sets navigation values via reflection and builds a materializer.")]
    public void Load()
    {
        if (TryBuildQuery(out var target, out var statement) is false)
        {
            AssignEmpty(target);
            return;
        }
        var rows = _context.Executor.Query(statement!, Materializer(target!));
        Assign(target!, rows);
    }

    [RequiresUnreferencedCode("Reads/sets navigation values via reflection and builds a materializer.")]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (TryBuildQuery(out var target, out var statement) is false)
        {
            AssignEmpty(target);
            return;
        }
        var rows = await _context.Executor.QueryAsync(statement!, Materializer(target!), cancellationToken).ConfigureAwait(false);
        Assign(target!, rows);
    }

    private bool TryBuildQuery(out EntityModel? target, out SqlStatement? statement)
    {
        target = _context.Model.GetEntity(_nav.TargetClrType);
        statement = null;

        // The local key is the FK (reference) or the principal key (collection) on this entity.
        var localColumn = _model.Columns.First(c => c.ColumnName == _nav.LocalKeyColumnName);
        var localValue = localColumn.Property.GetValue(_entity);
        if (localValue is null)
            return false;

        var dialect = _context.Dialect;
        var columns = string.Join(", ", target.Columns.Select(c => dialect.QuoteIdentifier(c.ColumnName)));
        var sql = $"SELECT {columns} FROM {dialect.QuoteQualifiedName(target.Schema, target.TableName)} " +
                  $"WHERE {dialect.QuoteIdentifier(_nav.TargetKeyColumnName)} = {dialect.RenderParameter(0)}";
        statement = new SqlStatement(sql, new[] { new SqlParameterBinding(localValue, localValue.GetType()) });
        return true;
    }

    [RequiresUnreferencedCode("Builds a materializer for the target entity.")]
    private static IMaterializer<object> Materializer(EntityModel target) =>
        new DelegateMaterializer<object>(RuntimeMaterializerFactory.BuildEntityObject(target));

    private void Assign(EntityModel target, List<object> rows)
    {
        if (_nav.Kind == NavigationKind.Reference)
        {
            _nav.Property.SetValue(_entity, rows.Count > 0 ? rows[0] : null);
            return;
        }
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_nav.TargetClrType))!;
        foreach (var row in rows) list.Add(row);
        _nav.Property.SetValue(_entity, list);
    }

    private void AssignEmpty(EntityModel? target)
    {
        if (_nav.Kind == NavigationKind.Reference)
            _nav.Property.SetValue(_entity, null);
        else
            _nav.Property.SetValue(_entity, Activator.CreateInstance(typeof(List<>).MakeGenericType(_nav.TargetClrType)));
    }
}
