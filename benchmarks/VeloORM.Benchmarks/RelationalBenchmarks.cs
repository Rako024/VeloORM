using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

namespace VeloORM.Benchmarks;

[Table("bench_users")]
public class BenchUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<BenchOrder>? Orders { get; set; }
}

[Table("bench_rel_products")]
public class BenchProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

[Table("bench_orders")]
public class BenchOrder
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public BenchUser? User { get; set; }
    public ICollection<BenchOrderItem>? Items { get; set; }
}

[Table("bench_order_items")]
public class BenchOrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public BenchOrder? Order { get; set; }
    public BenchProduct? Product { get; set; }
}

public class BenchRelEfContext(string connectionString) : DbContext
{
    private readonly string _cs = connectionString;
    public DbSet<BenchUser> Users => Set<BenchUser>();
    public DbSet<BenchProduct> Products => Set<BenchProduct>();
    public DbSet<BenchOrder> Orders => Set<BenchOrder>();
    public DbSet<BenchOrderItem> Items => Set<BenchOrderItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(_cs);

    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b)
    {
        var u = b.Entity<BenchUser>();
        u.ToTable("bench_users");
        u.Property(x => x.Id).HasColumnName("id");
        u.Property(x => x.Name).HasColumnName("name");

        var p = b.Entity<BenchProduct>();
        p.ToTable("bench_rel_products");
        p.Property(x => x.Id).HasColumnName("id");
        p.Property(x => x.Name).HasColumnName("name");
        p.Property(x => x.Price).HasColumnName("price");

        var o = b.Entity<BenchOrder>();
        o.ToTable("bench_orders");
        o.Property(x => x.Id).HasColumnName("id");
        o.Property(x => x.UserId).HasColumnName("user_id");
        o.Property(x => x.CreatedAt).HasColumnName("created_at");
        o.HasOne(x => x.User).WithMany(x => x.Orders).HasForeignKey(x => x.UserId);

        var i = b.Entity<BenchOrderItem>();
        i.ToTable("bench_order_items");
        i.Property(x => x.Id).HasColumnName("id");
        i.Property(x => x.OrderId).HasColumnName("order_id");
        i.Property(x => x.ProductId).HasColumnName("product_id");
        i.Property(x => x.Quantity).HasColumnName("quantity");
        i.HasOne(x => x.Order).WithMany(x => x.Items).HasForeignKey(x => x.OrderId);
        i.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
    }
}

/// <summary>Related-tables scenarios over User → Order → OrderItem → Product:
/// (1) reference Include (orders + their user), (2) multi-level ThenInclude (orders + items + each item's
/// product). Dapper hand-writes the join + multi-map; EF Core and VeloORM use Include/ThenInclude.</summary>
[MemoryDiagnoser]
public class RelationalBenchmarks
{
    [Params(1000, 100000)]
    public int N;

    private string _cs = "";
    private VeloDbContext _velo = null!;
    private NpgsqlConnection _dapper = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cs = BenchSeed.ConnectionString;
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        var factory = new NpgsqlConnectionFactory(_cs);
        _velo = new VeloDbContext(
            VeloModel.Build([typeof(BenchUser), typeof(BenchProduct), typeof(BenchOrder), typeof(BenchOrderItem)]),
            PostgresDialect.Instance, factory, new PostgresCommandExecutor(factory));

        _dapper = new NpgsqlConnection(_cs);
        _dapper.Open();
        BenchSeed.SeedRelational(_dapper, N);
    }

    [GlobalCleanup]
    public void Cleanup() => _dapper.Dispose();

    // ---- reference Include: orders + their user ----
    [Benchmark(Baseline = true)]
    public int Ado_ReferenceInclude()
    {
        using var cmd = _dapper.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.user_id, o.created_at, u.id, u.name
            FROM bench_orders o JOIN bench_users u ON o.user_id = u.id
            """;
        using var r = cmd.ExecuteReader();
        int count = 0;
        while (r.Read())
        {
            _ = new BenchOrder
            {
                Id = r.GetInt32(0), UserId = r.GetInt32(1), CreatedAt = r.GetFieldValue<DateTimeOffset>(2),
                User = new BenchUser { Id = r.GetInt32(3), Name = r.GetString(4) },
            };
            count++;
        }
        return count;
    }

    [Benchmark]
    public int Dapper_ReferenceInclude()
    {
        const string sql = """
            SELECT o.id, o.user_id, o.created_at, u.id, u.name
            FROM bench_orders o JOIN bench_users u ON o.user_id = u.id
            """;
        return _dapper.Query<BenchOrder, BenchUser, BenchOrder>(
            sql, (o, user) => { o.User = user; return o; }, splitOn: "id").Count();
    }

    [Benchmark]
    public int EfCore_ReferenceInclude()
    {
        // Include is qualified explicitly: EF Core and VeloORM both define an IQueryable Include in scope.
        using var ctx = new BenchRelEfContext(_cs);
        var q = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .Include(ctx.Orders.AsNoTracking(), o => o.User);
        return q.ToList().Count;
    }

    [Benchmark]
    public int Velo_ReferenceInclude() =>
        VeloORM.Runtime.VeloQueryableExtensions
            .Include(_velo.Set<BenchOrder>(), o => o.User).ToList().Count;

    // ---- multi-level ThenInclude: orders + items + each item's product ----
    [Benchmark]
    public int Ado_ThenInclude()
    {
        using var cmd = _dapper.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.user_id, o.created_at,
                   i.id, i.order_id, i.product_id, i.quantity,
                   p.id, p.name, p.price
            FROM bench_orders o
            JOIN bench_order_items i ON i.order_id = o.id
            JOIN bench_rel_products p ON p.id = i.product_id
            """;
        using var r = cmd.ExecuteReader();
        var orders = new Dictionary<int, BenchOrder>();
        while (r.Read())
        {
            int oid = r.GetInt32(0);
            if (!orders.TryGetValue(oid, out var order))
            {
                order = new BenchOrder
                {
                    Id = oid, UserId = r.GetInt32(1), CreatedAt = r.GetFieldValue<DateTimeOffset>(2),
                    Items = new List<BenchOrderItem>(),
                };
                orders.Add(oid, order);
            }
            order.Items!.Add(new BenchOrderItem
            {
                Id = r.GetInt32(3), OrderId = r.GetInt32(4), ProductId = r.GetInt32(5), Quantity = r.GetInt32(6),
                Product = new BenchProduct { Id = r.GetInt32(7), Name = r.GetString(8), Price = r.GetDecimal(9) },
            });
        }
        return orders.Count;
    }

    [Benchmark]
    public int Dapper_ThenInclude()
    {
        const string sql = """
            SELECT o.id, o.user_id, o.created_at,
                   i.id, i.order_id, i.product_id, i.quantity,
                   p.id, p.name, p.price
            FROM bench_orders o
            JOIN bench_order_items i ON i.order_id = o.id
            JOIN bench_rel_products p ON p.id = i.product_id
            """;
        var orders = new Dictionary<int, BenchOrder>();
        _dapper.Query<BenchOrder, BenchOrderItem, BenchProduct, BenchOrder>(
            sql,
            (o, item, prod) =>
            {
                if (!orders.TryGetValue(o.Id, out var order))
                {
                    order = o;
                    order.Items = new List<BenchOrderItem>();
                    orders.Add(order.Id, order);
                }
                item.Product = prod;
                order.Items!.Add(item);
                return order;
            },
            splitOn: "id,id").AsList();
        return orders.Count;
    }

    [Benchmark]
    public int EfCore_ThenInclude()
    {
        using var ctx = new BenchRelEfContext(_cs);
        var q = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .Include(ctx.Orders.AsNoTracking(), o => o.Items);
#pragma warning disable CS8620 // EF Core's ThenInclude signature is non-null over a nullable collection nav.
        return q.ThenInclude(i => i.Product).ToList().Count;
#pragma warning restore CS8620
    }

    [Benchmark]
    public int Velo_ThenInclude() =>
        VeloORM.Runtime.VeloQueryableExtensions
            .Include(_velo.Set<BenchOrder>(), o => o.Items)
            .ThenInclude(i => i.Product).ToList().Count;
}
