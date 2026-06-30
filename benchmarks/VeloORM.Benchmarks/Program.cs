using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

BenchmarkRunner.Run<SelectBenchmarks>();

[Table("bench_products")]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class BenchEfContext(string connectionString) : DbContext
{
    private readonly string _cs = connectionString;
    public DbSet<Product> Products => Set<Product>();
    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(_cs);
    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b) =>
        b.Entity<Product>().ToTable("bench_products");
}

[MemoryDiagnoser]
public class SelectBenchmarks
{
    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _dapperConn = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cs = Environment.GetEnvironmentVariable("VELO_CONNECTION")
              ?? "Host=localhost;Port=5432;Username=velo;Password=velo_dev_password;Database=veloorm";

        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        var factory = new NpgsqlConnectionFactory(_cs);
        _velo = new VeloDbContext(VeloModel.Build([typeof(Product)]), PostgresDialect.Instance,
            factory, new PostgresCommandExecutor(factory));

        _dapperConn = new NpgsqlConnection(_cs);
        _dapperConn.Open();

        _dapperConn.Execute("""
            DROP TABLE IF EXISTS bench_products;
            CREATE TABLE bench_products (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, price numeric NOT NULL,
                in_stock boolean NOT NULL, created_at timestamptz NOT NULL);
            INSERT INTO bench_products (name, price, in_stock, created_at)
            SELECT 'P' || g, (g % 100)::numeric, g % 2 = 0, now()
            FROM generate_series(1, 1000) g;
            """);
    }

    [Benchmark(Baseline = true)]
    public int Dapper_SelectAll() =>
        _dapperConn.Query<Product>("SELECT id, name, price, in_stock, created_at FROM bench_products").Count();

    [Benchmark]
    public int EfCore_SelectAll()
    {
        using var ctx = new BenchEfContext(_cs);
        return ctx.Products.AsNoTracking().ToList().Count;
    }

    [Benchmark]
    public int VeloORM_SelectAll() => _velo.Set<Product>().ToList().Count;
}
