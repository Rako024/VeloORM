using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

[Table("bench_articles")]
public class BenchArticle
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public ICollection<BenchLabel>? Labels { get; set; }
}

[Table("bench_labels")]
public class BenchLabel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<BenchArticle>? Articles { get; set; }
}

public class BenchM2MEfContext(string connectionString) : DbContext
{
    private readonly string _cs = connectionString;
    public DbSet<BenchArticle> Articles => Set<BenchArticle>();
    public DbSet<BenchLabel> Labels => Set<BenchLabel>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseNpgsql(_cs);
    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b)
    {
        var a = b.Entity<BenchArticle>();
        a.ToTable("bench_articles");
        a.Property(x => x.Id).HasColumnName("id");
        a.Property(x => x.Title).HasColumnName("title");

        var l = b.Entity<BenchLabel>();
        l.ToTable("bench_labels");
        l.Property(x => x.Id).HasColumnName("id");
        l.Property(x => x.Name).HasColumnName("name");

        a.HasMany(x => x.Labels).WithMany(x => x.Articles)
            .UsingEntity(
                "bench_article_labels",
                r => r.HasOne(typeof(BenchLabel)).WithMany().HasForeignKey("label_id"),
                l2 => l2.HasOne(typeof(BenchArticle)).WithMany().HasForeignKey("article_id"));
    }
}

/// <summary>Many-to-many include (articles + their labels through a junction table). VeloORM uses one
/// junction-join follow-up query; EF Core uses a skip navigation; Dapper and ADO.NET hand-write the
/// two-join query and group by article.</summary>
[MemoryDiagnoser]
public class ManyToManyBenchmarks
{
    private const int Articles = 2000;
    private const int Labels = 200;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _conn = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cs = BenchSeed.ConnectionString;
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        var factory = new NpgsqlConnectionFactory(_cs);
        _velo = new VeloDbContext(
            VeloModel.Build([typeof(BenchArticle), typeof(BenchLabel)],
                b => b.Entity<BenchArticle>().HasManyToMany(x => x.Labels!, "bench_article_labels", "article_id", "label_id")),
            PostgresDialect.Instance, factory, new PostgresCommandExecutor(factory));

        _conn = new NpgsqlConnection(_cs);
        _conn.Open();
        _conn.Execute($"""
            DROP TABLE IF EXISTS bench_article_labels CASCADE;
            DROP TABLE IF EXISTS bench_articles CASCADE;
            DROP TABLE IF EXISTS bench_labels CASCADE;
            CREATE TABLE bench_articles (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, title text NOT NULL);
            CREATE TABLE bench_labels (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
            CREATE TABLE bench_article_labels (
                article_id integer NOT NULL REFERENCES bench_articles(id),
                label_id integer NOT NULL REFERENCES bench_labels(id),
                PRIMARY KEY (article_id, label_id));
            INSERT INTO bench_articles (title) SELECT 'A' || g FROM generate_series(1, {Articles}) g;
            INSERT INTO bench_labels (name) SELECT 'L' || g FROM generate_series(1, {Labels}) g;
            INSERT INTO bench_article_labels (article_id, label_id)
                SELECT g, 1 + ((g + s * 131) % {Labels}) FROM generate_series(1, {Articles}) g, generate_series(0, 4) s
                ON CONFLICT DO NOTHING;
            """);
    }

    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    [Benchmark(Baseline = true)]
    public int Ado_ManyToMany()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.title, l.id, l.name
            FROM bench_articles a
            JOIN bench_article_labels j ON j.article_id = a.id
            JOIN bench_labels l ON l.id = j.label_id
            """;
        using var r = cmd.ExecuteReader();
        var articles = new Dictionary<int, BenchArticle>();
        while (r.Read())
        {
            int aid = r.GetInt32(0);
            if (!articles.TryGetValue(aid, out var a))
                articles.Add(aid, a = new BenchArticle { Id = aid, Title = r.GetString(1), Labels = new List<BenchLabel>() });
            a.Labels!.Add(new BenchLabel { Id = r.GetInt32(2), Name = r.GetString(3) });
        }
        return articles.Values.Sum(a => a.Labels!.Count);
    }

    [Benchmark]
    public int Dapper_ManyToMany()
    {
        const string sql = """
            SELECT a.id, a.title, l.id, l.name
            FROM bench_articles a
            JOIN bench_article_labels j ON j.article_id = a.id
            JOIN bench_labels l ON l.id = j.label_id
            """;
        var articles = new Dictionary<int, BenchArticle>();
        _conn.Query<BenchArticle, BenchLabel, BenchArticle>(sql, (a, l) =>
        {
            if (!articles.TryGetValue(a.Id, out var existing))
            {
                existing = a;
                existing.Labels = new List<BenchLabel>();
                articles.Add(a.Id, existing);
            }
            existing.Labels!.Add(l);
            return existing;
        }, splitOn: "id").AsList();
        return articles.Values.Sum(a => a.Labels!.Count);
    }

    [Benchmark]
    public int EfCore_ManyToMany()
    {
        using var ctx = new BenchM2MEfContext(_cs);
        var articles = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .Include(ctx.Articles.AsNoTracking(), a => a.Labels).ToList();
        return articles.Sum(a => a.Labels!.Count);
    }

    [Benchmark]
    public int Velo_ManyToMany()
    {
        var articles = VeloORM.Runtime.VeloQueryableExtensions
            .Include(_velo.Set<BenchArticle>(), a => a.Labels).ToList();
        return articles.Sum(a => a.Labels!.Count);
    }
}
