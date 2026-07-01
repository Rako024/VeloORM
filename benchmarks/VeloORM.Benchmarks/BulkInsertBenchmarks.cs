using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

[Table("bench_bulk")]
public class BulkRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Value { get; set; }
}

public class BenchBulkEfContext(string connectionString) : DbContext
{
    private readonly string _cs = connectionString;
    public DbSet<BulkRow> Rows => Set<BulkRow>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseNpgsql(_cs);
    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b)
    {
        var r = b.Entity<BulkRow>();
        r.ToTable("bench_bulk");
        r.Property(x => x.Id).HasColumnName("id");
        r.Property(x => x.Name).HasColumnName("name");
        r.Property(x => x.Value).HasColumnName("value");
    }
}

/// <summary>Bulk insert of N rows. VeloORM uses binary <c>COPY</c>; ADO.NET shows raw <c>COPY</c> as the
/// floor; EF Core batches INSERTs (AddRange + SaveChanges); Dapper runs one INSERT per row. Each
/// iteration starts from an empty table (excluded <see cref="IterationSetup"/>).</summary>
[MemoryDiagnoser]
public class BulkInsertBenchmarks
{
    [Params(20000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _conn = null!;
    private EntityModel _model = null!;
    private List<BulkRow> _rows = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cs = BenchSeed.ConnectionString;
        var factory = new NpgsqlConnectionFactory(_cs);
        var veloModel = VeloModel.Build([typeof(BulkRow)]);
        _model = veloModel.GetEntity<BulkRow>();
        _velo = new VeloDbContext(veloModel, PostgresDialect.Instance, factory, new PostgresCommandExecutor(factory));
        _conn = new NpgsqlConnection(_cs);
        _conn.Open();
        _conn.Execute("""
            DROP TABLE IF EXISTS bench_bulk;
            CREATE TABLE bench_bulk (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, value numeric NOT NULL);
            """);
        _rows = Enumerable.Range(1, N).Select(i => new BulkRow { Name = "B" + i, Value = i % 100 }).ToList();
    }

    [IterationSetup]
    public void Reset() => _conn.Execute("TRUNCATE bench_bulk RESTART IDENTITY");

    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    [Benchmark(Baseline = true)]
    public void Ado_Copy()
    {
        using var writer = _conn.BeginBinaryImport("COPY bench_bulk (name, value) FROM STDIN (FORMAT BINARY)");
        foreach (var row in _rows)
        {
            writer.StartRow();
            writer.Write(row.Name, NpgsqlDbType.Text);
            writer.Write(row.Value, NpgsqlDbType.Numeric);
        }
        writer.Complete();
    }

    [Benchmark]
    public void Dapper_PerRowInsert() =>
        _conn.Execute("INSERT INTO bench_bulk (name, value) VALUES (@Name, @Value)", _rows);

    [Benchmark]
    public void EfCore_AddRange()
    {
        using var ctx = new BenchBulkEfContext(_cs);
        ctx.Rows.AddRange(_rows.Select(r => new BulkRow { Name = r.Name, Value = r.Value }));
        ctx.SaveChanges();
    }

    [Benchmark]
    public void Velo_BulkInsert() => _velo.BulkInsert(_rows);
}
