using System.Runtime.CompilerServices;
using System.Text;
using VeloORM.Query;

namespace VeloORM.Runtime;

/// <summary>
/// One optional WHERE fragment, built from an interpolated string. Interpolation holes become bound
/// parameters (marked with a sentinel and renumbered during assembly), so values can never be spliced
/// into SQL. Uses the conditional interpolated-string-handler pattern: when its gate is false the
/// string is never even built.
/// </summary>
[InterpolatedStringHandler]
public ref struct VeloFragment
{
    // Control char (0x01) marking parameter positions inside a fragment template; renumbered on assembly.
    internal const char ParameterSentinel = (char)1;

    private readonly StringBuilder? _sql;
    private readonly List<SqlParameterBinding>? _parameters;

    public VeloFragment(int literalLength, int formattedCount, bool condition, out bool shouldAppend)
    {
        shouldAppend = condition;
        if (condition)
        {
            _sql = new StringBuilder(literalLength + formattedCount);
            _parameters = new List<SqlParameterBinding>(formattedCount);
        }
        else
        {
            _sql = null;
            _parameters = null;
        }
    }

    public void AppendLiteral(string value) => _sql!.Append(value);

    public void AppendFormatted<T>(T value)
    {
        _sql!.Append(ParameterSentinel);
        _parameters!.Add(new SqlParameterBinding(value, typeof(T)));
    }

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value);

    internal bool IsActive => _sql is not null;
    internal string Template => _sql?.ToString() ?? "";
    internal IReadOnlyList<SqlParameterBinding> Parameters =>
        _parameters ?? (IReadOnlyList<SqlParameterBinding>)Array.Empty<SqlParameterBinding>();
}
