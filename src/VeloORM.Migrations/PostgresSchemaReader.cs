using System.Data;
using System.Data.Common;
using VeloORM.Data;
using VeloORM.Migrations.Schema;

namespace VeloORM.Migrations;

/// <summary>Reads the current schema (tables, columns, primary keys, secondary indexes) from a live
/// PostgreSQL database via the catalog / information_schema.</summary>
public sealed class PostgresSchemaReader
{
    private readonly IConnectionFactory _connectionFactory;

    public PostgresSchemaReader(IConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public SchemaModel Read()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var tables = new List<SchemaTable>();
        foreach (var (schema, name) in ReadTableNames(connection))
        {
            var columns = ReadColumns(connection, schema, name);
            var pk = ReadPrimaryKey(connection, schema, name);
            var indexes = ReadIndexes(connection, schema, name);
            var foreignKeys = ReadForeignKeys(connection, schema, name);
            tables.Add(new SchemaTable
            {
                Name = name,
                Schema = schema,
                Columns = columns,
                PrimaryKey = pk,
                Indexes = indexes,
                ForeignKeys = foreignKeys,
            });
        }
        return new SchemaModel { Tables = tables };
    }

    private static List<(string Schema, string Name)> ReadTableNames(DbConnection conn)
    {
        const string sql = """
            SELECT table_schema, table_name FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
              AND table_name <> '__velo_migrations_history'
            ORDER BY table_schema, table_name;
            """;
        var result = new List<(string, string)>();
        using var reader = Query(conn, sql);
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static List<SchemaColumn> ReadColumns(DbConnection conn, string schema, string table)
    {
        var sql = $"""
            SELECT column_name, udt_name, is_nullable, is_identity
            FROM information_schema.columns
            WHERE table_schema = '{schema}' AND table_name = '{table}'
            ORDER BY ordinal_position;
            """;
        var columns = new List<SchemaColumn>();
        using var reader = Query(conn, sql);
        while (reader.Read())
        {
            columns.Add(new SchemaColumn
            {
                Name = reader.GetString(0),
                StoreType = CanonicalType(reader.GetString(1)),
                IsNullable = reader.GetString(2) == "YES",
                IsIdentity = reader.GetString(3) == "YES",
            });
        }
        return columns;
    }

    private static List<string> ReadPrimaryKey(DbConnection conn, string schema, string table)
    {
        var sql = $"""
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema = '{schema}' AND tc.table_name = '{table}'
            ORDER BY kcu.ordinal_position;
            """;
        var pk = new List<string>();
        using var reader = Query(conn, sql);
        while (reader.Read())
            pk.Add(reader.GetString(0));
        return pk;
    }

    private static List<SchemaIndex> ReadIndexes(DbConnection conn, string schema, string table)
    {
        // Exclude the primary-key index; compare secondary indexes by name.
        var sql = $"""
            SELECT indexname, indexdef FROM pg_indexes
            WHERE schemaname = '{schema}' AND tablename = '{table}';
            """;
        var indexes = new List<SchemaIndex>();
        using var reader = Query(conn, sql);
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var def = reader.GetString(1);
            if (name.EndsWith("_pkey", StringComparison.Ordinal))
                continue;
            indexes.Add(new SchemaIndex
            {
                Name = name,
                Columns = ParseIndexColumns(def),
                IsUnique = def.Contains("UNIQUE INDEX", StringComparison.OrdinalIgnoreCase),
            });
        }
        return indexes;
    }

    private static List<SchemaForeignKey> ReadForeignKeys(DbConnection conn, string schema, string table)
    {
        // One row per FK column; group by constraint name to preserve composite-key column order.
        var sql = $"""
            SELECT tc.constraint_name, kcu.column_name, kcu.ordinal_position,
                   ccu.table_schema AS principal_schema, ccu.table_name AS principal_table, ccu.column_name AS principal_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = '{schema}' AND tc.table_name = '{table}'
            ORDER BY tc.constraint_name, kcu.ordinal_position;
            """;

        var byName = new Dictionary<string, (List<string> Cols, string PrincipalSchema, string PrincipalTable, List<string> PrincipalCols)>();
        using (var reader = Query(conn, sql))
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (!byName.TryGetValue(name, out var entry))
                    byName[name] = entry = (new List<string>(), reader.GetString(3), reader.GetString(4), new List<string>());
                entry.Cols.Add(reader.GetString(1));
                entry.PrincipalCols.Add(reader.GetString(5));
            }
        }

        return byName.Select(kv => new SchemaForeignKey
        {
            Name = kv.Key,
            Columns = kv.Value.Cols,
            PrincipalSchema = kv.Value.PrincipalSchema,
            PrincipalTable = kv.Value.PrincipalTable,
            PrincipalColumns = kv.Value.PrincipalCols,
        }).ToList();
    }

    /// <summary>Extracts the column list from a pg_indexes <c>indexdef</c> (the parenthesized list).</summary>
    private static List<string> ParseIndexColumns(string indexDef)
    {
        var open = indexDef.LastIndexOf('(');
        var close = indexDef.LastIndexOf(')');
        if (open < 0 || close <= open)
            return new List<string>();

        var inner = indexDef.Substring(open + 1, close - open - 1);
        return inner.Split(',')
            .Select(c => c.Trim().Trim('"'))
            .Where(c => c.Length > 0)
            .ToList();
    }

    private static DbDataReader Query(DbConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteReader(CommandBehavior.Default);
    }

    /// <summary>Maps a PostgreSQL <c>udt_name</c> to the canonical store type produced by the dialect.</summary>
    private static string CanonicalType(string udtName) => udtName switch
    {
        "bool" => "boolean",
        "int2" => "smallint",
        "int4" => "integer",
        "int8" => "bigint",
        "float4" => "real",
        "float8" => "double precision",
        "timestamp" => "timestamp",
        "timestamptz" => "timestamptz",
        "bpchar" => "char",
        _ => udtName, // numeric, text, varchar, uuid, date, time, interval, bytea, etc.
    };
}
