using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using VeloORM.Query;

namespace VeloORM.Runtime;

/// <summary>
/// Builds a query from a base SELECT plus a set of optional, bool-gated WHERE fragments. Only the
/// <em>active</em> fragments are assembled (cheap string concat), and the assembled SQL is cached by
/// the active-fragment bitmask — so n optional filters cost n fragments, never 2ⁿ query variants.
/// Every fragment value is a bound parameter.
/// </summary>
public sealed class FragmentQuery<T>
{
    private readonly VeloDbContext _context;
    private readonly string _baseSelect;
    private readonly List<(string Template, IReadOnlyList<SqlParameterBinding> Parameters)> _active = new();
    private long _bitmask;
    private int _slot;

    internal FragmentQuery(VeloDbContext context, string baseSelect)
    {
        _context = context;
        _baseSelect = baseSelect;
    }

    /// <summary>Adds a WHERE fragment when <paramref name="condition"/> is true; otherwise the
    /// interpolated string is never built (conditional handler).</summary>
    public FragmentQuery<T> AndWhere(
        bool condition,
        [InterpolatedStringHandlerArgument("condition")] VeloFragment fragment)
    {
        if (condition && fragment.IsActive)
        {
            _active.Add((fragment.Template, fragment.Parameters));
            _bitmask |= 1L << _slot;
        }
        _slot++;
        return this;
    }

    [RequiresUnreferencedCode("Builds a materializer via reflection for the result type.")]
    public List<T> ToList()
    {
        var (sql, parameters) = Assemble();
        return _context.Executor.Query(new SqlStatement(sql, parameters), _context.RawMaterializerFor<T>());
    }

    [RequiresUnreferencedCode("Builds a materializer via reflection for the result type.")]
    public Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var (sql, parameters) = Assemble();
        return _context.Executor.QueryAsync(new SqlStatement(sql, parameters), _context.RawMaterializerFor<T>(), cancellationToken);
    }

    private (string Sql, SqlParameterBinding[] Parameters) Assemble()
    {
        // Assembled SQL depends only on the base + which fragments are active (the bitmask); cache it.
        var sql = _context.GetOrAssembleFragmentSql(_baseSelect, _bitmask, _active, _context.Dialect.RenderParameter);

        // Values are collected fresh each call, in fragment order (matching the renumbered $N).
        var parameters = new List<SqlParameterBinding>();
        foreach (var (_, fragmentParams) in _active)
            parameters.AddRange(fragmentParams);
        return (sql, parameters.ToArray());
    }
}
