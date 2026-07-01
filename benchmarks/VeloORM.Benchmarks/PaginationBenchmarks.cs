using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

/// <summary>Ordered pagination (<c>ORDER BY price OFFSET k LIMIT 20</c>) — the typical "page N" query.
/// The offset is a runtime value, so VeloORM goes through its runtime engine here (the constant-page and
/// compiled-parameter forms are covered by the interceptor and compiled-query benchmarks).</summary>
[MemoryDiagnoser]
public class PaginationBenchmarks
{
    private const int PageSize = 20;

    [Params(100000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _conn = null!;
    private int _skip;

    [GlobalSetup]
    public void Setup()
    {
        _cs = BenchSeed.ConnectionString;
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        var factory = new NpgsqlConnectionFactory(_cs);
        _velo = new VeloDbContext(VeloModel.Build([typeof(Product)]), PostgresDialect.Instance,
            factory, new PostgresCommandExecutor(factory));
        _conn = new NpgsqlConnection(_cs);
        _conn.Open();
        BenchSeed.SeedFlat(_conn, N);
        _skip = N / 2;
    }

    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    [Benchmark(Baseline = true)]
    public int Ado_Page()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, price, in_stock, created_at FROM bench_products ORDER BY price OFFSET $1 LIMIT $2";
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = _skip });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = PageSize });
        using var r = cmd.ExecuteReader();
        var list = new List<Product>();
        while (r.Read())
            list.Add(new Product
            {
                Id = r.GetInt32(0), Name = r.GetString(1), Price = r.GetDecimal(2),
                InStock = r.GetBoolean(3), CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
            });
        return list.Count;
    }

    [Benchmark]
    public int Dapper_Page() =>
        _conn.Query<Product>(
            "SELECT id, name, price, in_stock, created_at FROM bench_products ORDER BY price OFFSET @s LIMIT @t",
            new { s = _skip, t = PageSize }).Count();

    [Benchmark]
    public int EfCore_Page()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.AsNoTracking().OrderBy(x => x.Price).Skip(_skip).Take(PageSize).ToList().Count;
    }

    [Benchmark]
    public int Velo_Page() =>
        _velo.Set<Product>().OrderBy(x => x.Price).Skip(_skip).Take(PageSize).ToList().Count;
}
