using BenchmarkDotNet.Attributes;
using Dapper;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

/// <summary>Server-side aggregates over a single table: <c>Count</c>, <c>Sum</c>, <c>Average</c>.
/// None pull rows into memory. VeloORM's no-operator <c>Count()</c> / <c>Sum(selector)</c> run through the
/// compile-time interceptor (baked SQL, zero runtime translation); ADO/Dapper/EF are shown alongside.</summary>
[MemoryDiagnoser]
public class AggregateBenchmarks
{
    [Params(100000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _conn = null!;

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
    }

    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    private T Scalar<T>(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T))!;
    }

    // ---- Count ----
    [Benchmark(Baseline = true)]
    public long Ado_Count() => Scalar<long>("SELECT count(*) FROM bench_products");

    [Benchmark]
    public long Dapper_Count() => _conn.ExecuteScalar<long>("SELECT count(*) FROM bench_products");

    [Benchmark]
    public int EfCore_Count()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.Count();
    }

    [Benchmark]
    public int Velo_Count() => _velo.Set<Product>().Count();

    // ---- Sum ----
    [Benchmark]
    public decimal Ado_Sum() => Scalar<decimal>("SELECT coalesce(sum(price), 0) FROM bench_products");

    [Benchmark]
    public decimal Dapper_Sum() => _conn.ExecuteScalar<decimal>("SELECT coalesce(sum(price), 0) FROM bench_products");

    [Benchmark]
    public decimal EfCore_Sum()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.Sum(x => x.Price);
    }

    [Benchmark]
    public decimal Velo_Sum() => _velo.Set<Product>().Sum(x => x.Price);

    // ---- Average ----
    [Benchmark]
    public decimal Ado_Average() => Scalar<decimal>("SELECT coalesce(avg(price), 0) FROM bench_products");

    [Benchmark]
    public decimal Dapper_Average() => _conn.ExecuteScalar<decimal>("SELECT coalesce(avg(price), 0) FROM bench_products");

    [Benchmark]
    public decimal EfCore_Average()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.Average(x => x.Price);
    }

    [Benchmark]
    public decimal Velo_Average() => _velo.Set<Product>().Average(x => x.Price);
}
