using BenchmarkDotNet.Attributes;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

/// <summary>Bulk update of N rows by key. VeloORM stages into a temp table and applies one
/// <c>UPDATE … FROM</c>; ADO.NET shows the same temp-table approach by hand (the floor); Dapper runs one
/// UPDATE per row; EF Core loads, mutates, and SaveChanges (batched UPDATEs). Each iteration re-seeds the
/// table to a known state (excluded <see cref="IterationSetup"/>).</summary>
[MemoryDiagnoser]
public class BulkUpdateBenchmarks
{
    [Params(20000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _conn = null!;
    private List<BulkRow> _updates = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cs = BenchSeed.ConnectionString;
        var factory = new NpgsqlConnectionFactory(_cs);
        _velo = new VeloDbContext(VeloModel.Build([typeof(BulkRow)]), PostgresDialect.Instance,
            factory, new PostgresCommandExecutor(factory));
        _conn = new NpgsqlConnection(_cs);
        _conn.Open();
        _conn.Execute("""
            DROP TABLE IF EXISTS bench_bulk;
            CREATE TABLE bench_bulk (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL, value numeric NOT NULL);
            """);
        // ids 1..N exist after each reseed; these carry the new values.
        _updates = Enumerable.Range(1, N).Select(i => new BulkRow { Id = i, Name = "U" + i, Value = (i % 100) + 1000 }).ToList();
    }

    [IterationSetup]
    public void Reseed()
    {
        _conn.Execute("TRUNCATE bench_bulk RESTART IDENTITY");
        using var writer = _conn.BeginBinaryImport("COPY bench_bulk (name, value) FROM STDIN (FORMAT BINARY)");
        for (int i = 1; i <= N; i++)
        {
            writer.StartRow();
            writer.Write("B" + i, NpgsqlDbType.Text);
            writer.Write((decimal)(i % 100), NpgsqlDbType.Numeric);
        }
        writer.Complete();
    }

    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    [Benchmark(Baseline = true)]
    public void Ado_TempTableUpdate()
    {
        _conn.Execute("CREATE TEMP TABLE tmp_bulk (id integer, value numeric)");
        using (var writer = _conn.BeginBinaryImport("COPY tmp_bulk (id, value) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var row in _updates)
            {
                writer.StartRow();
                writer.Write(row.Id, NpgsqlDbType.Integer);
                writer.Write(row.Value, NpgsqlDbType.Numeric);
            }
            writer.Complete();
        }
        _conn.Execute("UPDATE bench_bulk b SET value = t.value FROM tmp_bulk t WHERE b.id = t.id");
        _conn.Execute("DROP TABLE tmp_bulk");
    }

    [Benchmark]
    public void Dapper_PerRowUpdate() =>
        _conn.Execute("UPDATE bench_bulk SET value = @Value WHERE id = @Id", _updates);

    [Benchmark]
    public void EfCore_LoadModifySave()
    {
        using var ctx = new BenchBulkEfContext(_cs);
        var rows = ctx.Rows.ToList();
        foreach (var row in rows) row.Value += 1000;
        ctx.SaveChanges();
    }

    [Benchmark]
    public void Velo_BulkUpdate() => _velo.BulkUpdate(_updates);
}
