namespace VeloORM.Scaffold;

/// <summary>Maps PostgreSQL store types (canonical form from the schema reader) back to CLR type names
/// for DB-first scaffolding — the reverse of the runtime type mapper.</summary>
public static class ScaffoldTypeMapper
{
    public static string ToClrTypeName(string storeType, bool nullable)
    {
        var baseType = StripModifiers(storeType);
        var (clr, isValueType) = Map(baseType);
        // Value types get a "?"; reference types are annotated nullable too for nullable columns.
        return nullable ? clr + "?" : clr;
    }

    private static (string Clr, bool IsValueType) Map(string baseType) => baseType switch
    {
        "boolean" or "bool" => ("bool", true),
        "smallint" or "int2" => ("short", true),
        "integer" or "int4" or "int" or "serial" => ("int", true),
        "bigint" or "int8" or "bigserial" => ("long", true),
        "real" or "float4" => ("float", true),
        "double precision" or "float8" => ("double", true),
        "numeric" or "decimal" or "money" => ("decimal", true),
        "uuid" => ("System.Guid", true),
        "timestamptz" or "timestamp with time zone" => ("System.DateTimeOffset", true),
        "timestamp" or "timestamp without time zone" => ("System.DateTime", true),
        "date" => ("System.DateOnly", true),
        "time" or "time without time zone" => ("System.TimeOnly", true),
        "interval" => ("System.TimeSpan", true),
        "bytea" => ("byte[]", false),
        "text" or "varchar" or "char" or "bpchar" or "citext" or "json" or "jsonb" => ("string", false),
        _ => ("string", false), // safe default
    };

    private static string StripModifiers(string storeType)
    {
        var t = storeType.Trim().ToLowerInvariant();
        var paren = t.IndexOf('(');
        return paren >= 0 ? t.Substring(0, paren).Trim() : t;
    }
}
