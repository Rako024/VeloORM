using Dapper;
using Npgsql;

namespace VeloORM.Benchmarks;

/// <summary>Shared SQL seeding used by the benchmark <c>[GlobalSetup]</c>s. All tables are dropped and
/// recreated, then filled with <c>generate_series</c> so seeding a large <paramref name="n"/> is fast.</summary>
internal static class BenchSeed
{
    public const int Dimensions = 1000; // fixed count of users / products that orders fan out over

    public static void SeedFlat(NpgsqlConnection conn, int n)
    {
        conn.Execute($"""
            DROP TABLE IF EXISTS bench_products;
            CREATE TABLE bench_products (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, price numeric NOT NULL,
                in_stock boolean NOT NULL, created_at timestamptz NOT NULL);
            INSERT INTO bench_products (name, price, in_stock, created_at)
            SELECT 'P' || g, (g % 100)::numeric, g % 2 = 0, now()
            FROM generate_series(1, {n}) g;
            """);
    }

    public static void SeedRelational(NpgsqlConnection conn, int n)
    {
        conn.Execute($"""
            DROP TABLE IF EXISTS bench_order_items CASCADE;
            DROP TABLE IF EXISTS bench_orders CASCADE;
            DROP TABLE IF EXISTS bench_rel_products CASCADE;
            DROP TABLE IF EXISTS bench_users CASCADE;

            CREATE TABLE bench_users (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
            CREATE TABLE bench_rel_products (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL, price numeric NOT NULL);
            CREATE TABLE bench_orders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                user_id integer NOT NULL REFERENCES bench_users(id),
                created_at timestamptz NOT NULL);
            CREATE TABLE bench_order_items (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                order_id integer NOT NULL REFERENCES bench_orders(id),
                product_id integer NOT NULL REFERENCES bench_rel_products(id),
                quantity integer NOT NULL);

            INSERT INTO bench_users (name) SELECT 'U' || g FROM generate_series(1, {Dimensions}) g;
            INSERT INTO bench_rel_products (name, price)
                SELECT 'RP' || g, (g % 100)::numeric FROM generate_series(1, {Dimensions}) g;
            INSERT INTO bench_orders (user_id, created_at)
                SELECT 1 + (g % {Dimensions}), now() FROM generate_series(1, {n}) g;
            INSERT INTO bench_order_items (order_id, product_id, quantity)
                SELECT 1 + (g % {n}), 1 + (g % {Dimensions}), 1 + (g % 5) FROM generate_series(1, {n}) g;

            CREATE INDEX ix_bench_orders_user ON bench_orders (user_id);
            CREATE INDEX ix_bench_items_order ON bench_order_items (order_id);
            CREATE INDEX ix_bench_items_product ON bench_order_items (product_id);
            """);
    }

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("VELO_CONNECTION")
        ?? "Host=localhost;Port=5432;Username=velo;Password=velo_dev_password;Database=veloorm";
}
