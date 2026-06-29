using NpgsqlTypes;
using VeloORM.Metadata;

namespace VeloORM.Postgres;

/// <summary>
/// Maps CLR types to PostgreSQL store types (for DDL) and to <see cref="NpgsqlDbType"/>
/// (so bound parameters — including nulls — carry an unambiguous type).
/// </summary>
public static class PostgresTypeMapper
{
    /// <summary>Returns the DDL store type for a column, honoring an explicit override.</summary>
    public static string GetStoreType(ColumnModel column)
    {
        if (!string.IsNullOrEmpty(column.StoreType))
            return column.StoreType!;

        return GetStoreType(column.ClrType);
    }

    /// <summary>Returns the base PostgreSQL type name for a (nullable-unwrapped) CLR type.</summary>
    public static string GetStoreType(Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (t.IsEnum)
            return "integer"; // enums stored as their underlying integral value by default

        return t switch
        {
            _ when t == typeof(bool) => "boolean",
            _ when t == typeof(byte) => "smallint",
            _ when t == typeof(sbyte) => "smallint",
            _ when t == typeof(short) => "smallint",
            _ when t == typeof(ushort) => "integer",
            _ when t == typeof(int) => "integer",
            _ when t == typeof(uint) => "bigint",
            _ when t == typeof(long) => "bigint",
            _ when t == typeof(ulong) => "numeric(20,0)",
            _ when t == typeof(float) => "real",
            _ when t == typeof(double) => "double precision",
            _ when t == typeof(decimal) => "numeric",
            _ when t == typeof(string) => "text",
            _ when t == typeof(char) => "text",
            _ when t == typeof(Guid) => "uuid",
            _ when t == typeof(DateTime) => "timestamp",
            _ when t == typeof(DateTimeOffset) => "timestamptz",
            _ when t == typeof(DateOnly) => "date",
            _ when t == typeof(TimeOnly) => "time",
            _ when t == typeof(TimeSpan) => "interval",
            _ when t == typeof(byte[]) => "bytea",
            _ => throw new NotSupportedException($"No PostgreSQL store type mapping for CLR type '{t}'."),
        };
    }

    /// <summary>Best-effort <see cref="NpgsqlDbType"/> for a CLR type; null means "let Npgsql infer".</summary>
    public static NpgsqlDbType? GetNpgsqlDbType(Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (t.IsEnum)
            return NpgsqlDbType.Integer;

        return t switch
        {
            _ when t == typeof(bool) => NpgsqlDbType.Boolean,
            _ when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => NpgsqlDbType.Smallint,
            _ when t == typeof(ushort) || t == typeof(int) => NpgsqlDbType.Integer,
            _ when t == typeof(uint) || t == typeof(long) => NpgsqlDbType.Bigint,
            _ when t == typeof(float) => NpgsqlDbType.Real,
            _ when t == typeof(double) => NpgsqlDbType.Double,
            _ when t == typeof(decimal) || t == typeof(ulong) => NpgsqlDbType.Numeric,
            _ when t == typeof(string) || t == typeof(char) => NpgsqlDbType.Text,
            _ when t == typeof(Guid) => NpgsqlDbType.Uuid,
            _ when t == typeof(DateTime) => NpgsqlDbType.Timestamp,
            _ when t == typeof(DateTimeOffset) => NpgsqlDbType.TimestampTz,
            _ when t == typeof(DateOnly) => NpgsqlDbType.Date,
            _ when t == typeof(TimeOnly) => NpgsqlDbType.Time,
            _ when t == typeof(TimeSpan) => NpgsqlDbType.Interval,
            _ when t == typeof(byte[]) => NpgsqlDbType.Bytea,
            _ => null,
        };
    }
}
