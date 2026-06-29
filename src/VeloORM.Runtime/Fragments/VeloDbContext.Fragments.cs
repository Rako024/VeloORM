using System.Collections.Concurrent;
using System.Text;
using VeloORM.Query;

namespace VeloORM.Runtime;

/// <summary>
/// Bool-gated optional-filter support. Entry point for the fragment engine plus the bitmask-keyed
/// cache of assembled SQL.
/// </summary>
public partial class VeloDbContext
{
    private readonly ConcurrentDictionary<(string Base, long Bitmask), string> _fragmentCache = new();
    private long _fragmentAssemblies;

    /// <summary>Number of distinct fragment combinations assembled (cache misses). Re-running the same
    /// active-filter combination does not re-assemble — it is keyed by the active-fragment bitmask.</summary>
    public long FragmentAssemblyCount => Interlocked.Read(ref _fragmentAssemblies);

    /// <summary>Starts an optional-filter query over a base SELECT (without a WHERE clause).</summary>
    public FragmentQuery<T> FilteredQuery<T>(string baseSelect) => new(this, baseSelect);

    internal string GetOrAssembleFragmentSql(
        string baseSelect,
        long bitmask,
        IReadOnlyList<(string Template, IReadOnlyList<SqlParameterBinding> Parameters)> activeFragments,
        Func<int, string> renderParameter)
    {
        return _fragmentCache.GetOrAdd((baseSelect, bitmask), _ =>
        {
            Interlocked.Increment(ref _fragmentAssemblies);
            return Assemble(baseSelect, activeFragments, renderParameter);
        });
    }

    private static string Assemble(
        string baseSelect,
        IReadOnlyList<(string Template, IReadOnlyList<SqlParameterBinding> Parameters)> activeFragments,
        Func<int, string> renderParameter)
    {
        if (activeFragments.Count == 0)
            return baseSelect;

        var sb = new StringBuilder(baseSelect.Length + 32);
        sb.Append(baseSelect).Append(" WHERE ");

        int ordinal = 0;
        for (int i = 0; i < activeFragments.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append('(');
            foreach (char c in activeFragments[i].Template)
            {
                if (c == VeloFragment.ParameterSentinel)
                    sb.Append(renderParameter(ordinal++)); // renumber $N across the assembled statement
                else
                    sb.Append(c);
            }
            sb.Append(')');
        }
        return sb.ToString();
    }
}
