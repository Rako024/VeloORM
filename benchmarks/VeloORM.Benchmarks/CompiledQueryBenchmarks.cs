using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;
using VQ = VeloORM.Runtime.Query;

namespace VeloORM.Benchmarks;

/// <summary>
/// Repeated parameterized point lookups (by id), the scenario compiled queries exist for. Each op runs
/// <see cref="Iterations"/> lookups with varying ids. VeloORM uses <c>Query.Compile</c> (baked SQL +
/// typed, boxing-free parameter binding); EF Core uses <c>EF.CompileQuery</c>; Dapper and ADO.NET reuse
/// a prepared statement. Highlights per-invocation overhead once compilation is amortized.
/// </summary>
[MemoryDiagnoser]
public class CompiledQueryBenchmarks
{
    private const int Iterations = 100;

    [Params(100000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _conn = null!;

    private Func<VeloDbContext, int, Product?> _veloCompiled = null!;
    private static readonly Func<BenchEfContext, int, Product?> EfCompiled =
        EF.CompileQuery((BenchEfContext ctx, int id) => ctx.Products.AsNoTracking().FirstOrDefault(p => p.Id == id));

    private NpgsqlCommand _adoCmd = null!;
    private NpgsqlParameter<int> _adoParam = null!;

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

        _veloCompiled = VQ.Compile<VeloDbContext, int, Product?>(
            (db, id) => db.Set<Product>().Where(p => p.Id == id).FirstOrDefault());

        _adoCmd = _conn.CreateCommand();
        _adoCmd.CommandText = "SELECT id, name, price, in_stock, created_at FROM bench_products WHERE id = $1";
        _adoParam = new NpgsqlParameter<int>();
        _adoCmd.Parameters.Add(_adoParam);
        _adoCmd.Prepare();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _adoCmd.Dispose();
        _conn.Dispose();
    }

    private int IdFor(int i) => 1 + (i * 997) % N;

    [Benchmark(Baseline = true)]
    public long Ado_Compiled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            _adoParam.TypedValue = IdFor(i);
            using var r = _adoCmd.ExecuteReader();
            if (r.Read()) sum += r.GetInt32(0);
        }
        return sum;
    }

    [Benchmark]
    public long Dapper_Compiled()
    {
        const string sql = "SELECT id, name, price, in_stock, created_at FROM bench_products WHERE id = @id";
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var p = _conn.QueryFirstOrDefault<Product>(sql, new { id = IdFor(i) });
            if (p is not null) sum += p.Id;
        }
        return sum;
    }

    [Benchmark]
    public long EfCore_Compiled()
    {
        using var ctx = new BenchEfContext(_cs);
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var p = EfCompiled(ctx, IdFor(i));
            if (p is not null) sum += p.Id;
        }
        return sum;
    }

    [Benchmark]
    public long Velo_Compiled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var p = _veloCompiled(_velo, IdFor(i));
            if (p is not null) sum += p.Id;
        }
        return sum;
    }
}
