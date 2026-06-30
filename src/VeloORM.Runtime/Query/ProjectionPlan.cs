using System.Reflection;
using VeloORM.Metadata;

namespace VeloORM.Runtime.Internal;

internal enum ProjectionKind
{
    /// <summary>Materialize the whole entity (no Select, or Select(x =&gt; x)).</summary>
    Entity,
    /// <summary>Materialize a single scalar column (Select(x =&gt; x.Prop)).</summary>
    Scalar,
    /// <summary>Materialize via a constructor over projected columns (Select(x =&gt; new {...}) / new Dto(...)).</summary>
    Constructor,
    /// <summary>Materialize the root entity plus reference navigations from a joined result set.</summary>
    EntityGraph,
}

/// <summary>A reference navigation materialized inline from a join (child columns aliased
/// <c>{AliasPrefix}_{column}</c>). <see cref="Children"/> holds nested reference includes
/// (<c>Include(o =&gt; o.A).ThenInclude(a =&gt; a.B)</c>), each joined and materialized recursively.</summary>
internal sealed class ReferenceInclude
{
    public required NavigationModel Navigation { get; init; }
    public required EntityModel Target { get; init; }
    public required string AliasPrefix { get; init; }
    public List<ReferenceInclude> Children { get; } = new();
}

/// <summary>Describes how to turn a result row into a CLR instance for the runtime materializer.</summary>
internal sealed class ProjectionPlan
{
    public required ProjectionKind Kind { get; init; }
    public required Type ResultType { get; init; }

    /// <summary>Set for <see cref="ProjectionKind.Entity"/>.</summary>
    public EntityModel? Entity { get; init; }

    /// <summary>Set for <see cref="ProjectionKind.Scalar"/>: the single output column alias.</summary>
    public string? ScalarAlias { get; init; }

    /// <summary>Set for <see cref="ProjectionKind.Constructor"/>.</summary>
    public ConstructorInfo? Constructor { get; init; }

    /// <summary>Constructor argument aliases/types in parameter order.</summary>
    public IReadOnlyList<(string Alias, Type Type)>? ConstructorArgs { get; init; }

    /// <summary>Set for <see cref="ProjectionKind.EntityGraph"/>: the root entity's column alias prefix
    /// (root columns are aliased <c>{RootAliasPrefix}_{column}</c>).</summary>
    public string? RootAliasPrefix { get; init; }

    /// <summary>Set for <see cref="ProjectionKind.EntityGraph"/>: reference navigations to populate.</summary>
    public IReadOnlyList<ReferenceInclude>? ReferenceIncludes { get; init; }
}
