using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

[Table("bench_products")]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class BenchEfContext(string connectionString) : DbContext
{
    private readonly string _cs = connectionString;
    public DbSet<Product> Products => Set<Product>();
    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(_cs);
    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b)
    {
        var p = b.Entity<Product>();
        p.ToTable("bench_products");
        p.Property(x => x.Id).HasColumnName("id");
        p.Property(x => x.Name).HasColumnName("name");
        p.Property(x => x.Price).HasColumnName("price");
        p.Property(x => x.InStock).HasColumnName("in_stock");
        p.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

/// <summary>Flat single-table scenarios: SELECT all, filtered WHERE, single-by-id, and a DTO projection,
/// each across Dapper / EF Core / VeloORM. SELECT-all additionally splits VeloORM into its interceptor
/// (compile-time) and runtime paths; the other shapes use operators/predicates and so always run through
/// the runtime engine.</summary>
[MemoryDiagnoser]
public class FlatBenchmarks
{
    [Params(1000, 100000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _dapper = null!;
    private int _midId;

    [GlobalSetup]
    public void Setup()
    {
        _cs = BenchSeed.ConnectionString;
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        var factory = new NpgsqlConnectionFactory(_cs);
        _velo = new VeloDbContext(VeloModel.Build([typeof(Product)]), PostgresDialect.Instance,
            factory, new PostgresCommandExecutor(factory));

        _dapper = new NpgsqlConnection(_cs);
        _dapper.Open();
        BenchSeed.SeedFlat(_dapper, N);
        _midId = N / 2;
    }

    [GlobalCleanup]
    public void Cleanup() => _dapper.Dispose();

    // ---- SELECT all ----
    [Benchmark(Baseline = true)]
    public int Dapper_SelectAll() =>
        _dapper.Query<Product>("SELECT id, name, price, in_stock, created_at FROM bench_products").Count();

    [Benchmark]
    public int EfCore_SelectAll()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.AsNoTracking().ToList().Count;
    }

    [Benchmark]
    public int Velo_Interceptor_SelectAll() => _velo.Set<Product>().ToList().Count;

    [Benchmark]
    public int Velo_Runtime_SelectAll() => ((IQueryable<Product>)_velo.Set<Product>()).ToList().Count;

    // ---- filtered WHERE ----
    [Benchmark]
    public int Dapper_Where() =>
        _dapper.Query<Product>(
            "SELECT id, name, price, in_stock, created_at FROM bench_products WHERE price > @p AND in_stock",
            new { p = 50m }).Count();

    [Benchmark]
    public int EfCore_Where()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.AsNoTracking().Where(x => x.Price > 50m && x.InStock).ToList().Count;
    }

    [Benchmark]
    public int Velo_Where() =>
        _velo.Set<Product>().Where(x => x.Price > 50m && x.InStock).ToList().Count;

    // ---- single by id ----
    [Benchmark]
    public Product? Dapper_SingleById() =>
        _dapper.QueryFirstOrDefault<Product>(
            "SELECT id, name, price, in_stock, created_at FROM bench_products WHERE id = @id", new { id = _midId });

    [Benchmark]
    public Product? EfCore_SingleById()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.AsNoTracking().FirstOrDefault(x => x.Id == _midId);
    }

    [Benchmark]
    public Product? Velo_SingleById() => _velo.Set<Product>().FirstOrDefault(x => x.Id == _midId);

    // ---- projection to DTO ----
    [Benchmark]
    public int Dapper_Projection() =>
        _dapper.Query<ProductDto>("SELECT id, name FROM bench_products").Count();

    [Benchmark]
    public int EfCore_Projection()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.AsNoTracking().Select(x => new ProductDto { Id = x.Id, Name = x.Name }).ToList().Count;
    }

    [Benchmark]
    public int Velo_Projection() =>
        // VeloORM projects to a constructor/anonymous type (NewExpression); same two-column shape as the DTO.
        _velo.Set<Product>().Select(x => new { x.Id, x.Name }).ToList().Count;
}
