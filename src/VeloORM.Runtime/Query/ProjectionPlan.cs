using System.Reflection;
using VeloORM.Metadata;

namespace VeloORM.Runtime.Query;

internal enum ProjectionKind
{
    /// <summary>Materialize the whole entity (no Select, or Select(x =&gt; x)).</summary>
    Entity,
    /// <summary>Materialize a single scalar column (Select(x =&gt; x.Prop)).</summary>
    Scalar,
    /// <summary>Materialize via a constructor over projected columns (Select(x =&gt; new {...}) / new Dto(...)).</summary>
    Constructor,
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
}
