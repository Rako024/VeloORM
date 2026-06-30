using VeloORM.Metadata;
using VeloORM.Migrations;
using VeloORM.Postgres;
using VeloORM.Runtime;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("VELO_CONNECTION")
    ?? "Host=localhost;Port=5432;Username=velo;Password=velo_dev_password;Database=veloorm";

// One shared, thread-safe context (pooled connections, concurrent shape cache).
var factory = new NpgsqlConnectionFactory(connectionString);
var db = new VeloDbContext(
    VeloModel.Build([typeof(Product)]),
    PostgresDialect.Instance,
    factory,
    new PostgresCommandExecutor(factory));
builder.Services.AddSingleton(db);
builder.Services.AddOpenApi();

var app = builder.Build();

// Code-first auto-migrate: diff the model against the live schema and apply if needed.
EnsureSchema(connectionString);

// OpenAPI document at /openapi/v1.json
app.MapOpenApi();

// --- Layer 1 + 2: static whole-table query (intercepted at compile time) ---
app.MapGet("/products", () => db.Set<Product>().ToList());

// --- Layer 3 (runtime engine): single entity by id ---
app.MapGet("/products/{id:int}", (int id) =>
{
    var product = db.Set<Product>().Where(p => p.Id == id).FirstOrDefault();
    return product is null ? Results.NotFound() : Results.Ok(product);
});

// --- Fragment layer: bool-gated optional filters ---
app.MapGet("/products/search", (string? name, decimal? minPrice, bool? inStock) =>
    db.FilteredQuery<Product>("SELECT id, name, price, in_stock, created_at FROM products")
      .AndWhere(name is not null, $"name = {name}")
      .AndWhere(minPrice.HasValue, $"price >= {minPrice}")
      .AndWhere(inStock.HasValue, $"in_stock = {inStock}")
      .ToList());

// --- Raw SQL escape hatch: interpolated values are bound, never concatenated ---
app.MapGet("/products/expensive", (decimal min) =>
    db.Query<Product>($"SELECT * FROM products WHERE price >= {min} ORDER BY price DESC"));

app.MapPost("/products", (CreateProduct input) =>
{
    var id = db.ExecuteScalar<int>(
        $"INSERT INTO products (name, price, in_stock, created_at) VALUES ({input.Name}, {input.Price}, {input.InStock}, now()) RETURNING id");
    return Results.Created($"/products/{id}", new { id });
});

app.MapGet("/health", () =>
{
    var ok = db.ExecuteScalar<int>($"SELECT 1") == 1;
    return ok ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503);
});

app.Run();

void EnsureSchema(string cs)
{
    var f = new NpgsqlConnectionFactory(cs);
    var scaffolder = new MigrationScaffolder(PostgresDialect.Instance);
    var migration = scaffolder.Create(VeloModel.Build([typeof(Product)]), new PostgresSchemaReader(f).Read(),
        "auto", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
    var migrator = new Migrator(f);
    migrator.EnsureHistoryTable();
    if (!scaffolder.IsEmpty(migration))
        migrator.Update([migration]);
}

[Table("products")]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public record CreateProduct(string Name, decimal Price, bool InStock);
