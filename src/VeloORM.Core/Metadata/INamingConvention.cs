using System.Text;

namespace VeloORM.Metadata;

/// <summary>
/// Translates CLR identifiers (PascalCase) to store identifiers and back.
/// Used to derive default table/column names and for DB-first scaffolding.
/// </summary>
public interface INamingConvention
{
    /// <summary>Default table name for an entity type name (e.g. <c>OrderItem</c>).</summary>
    string TableName(string typeName);

    /// <summary>Default column name for a property name (e.g. <c>CustomerId</c>).</summary>
    string ColumnName(string propertyName);
}

/// <summary>
/// The default PostgreSQL-friendly convention: PascalCase &lt;-&gt; snake_case,
/// with table names pluralized naively. e.g. <c>OrderItem</c> -&gt; <c>order_items</c>,
/// <c>CustomerId</c> -&gt; <c>customer_id</c>.
/// </summary>
public sealed class SnakeCaseNamingConvention : INamingConvention
{
    public static readonly SnakeCaseNamingConvention Instance = new();

    public string TableName(string typeName) => Pluralize(ToSnakeCase(typeName));

    public string ColumnName(string propertyName) => ToSnakeCase(propertyName);

    /// <summary>Converts PascalCase / camelCase to snake_case. Runs of capitals are kept together
    /// (e.g. <c>HTTPStatus</c> -&gt; <c>http_status</c>, <c>OrderID</c> -&gt; <c>order_id</c>).</summary>
    public static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                bool prevLower = i > 0 && char.IsLower(value[i - 1]);
                bool nextLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                if (i > 0 && (prevLower || nextLower))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Maps snake_case to PascalCase (used by scaffolding).</summary>
    public static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length);
        bool upperNext = true;
        foreach (char c in value)
        {
            if (c == '_')
            {
                upperNext = true;
                continue;
            }
            sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }
        return sb.ToString();
    }

    private static string Pluralize(string snake)
    {
        if (snake.EndsWith("s", StringComparison.Ordinal))
            return snake;
        if (snake.EndsWith("y", StringComparison.Ordinal) && snake.Length > 1 && !IsVowel(snake[snake.Length - 2]))
            return snake.Substring(0, snake.Length - 1) + "ies";
        return snake + "s";
    }

    private static bool IsVowel(char c) => "aeiou".IndexOf(c) >= 0;
}
