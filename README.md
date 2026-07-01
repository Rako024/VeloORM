# VeloORM

A high-performance, hybrid Object-Relational Mapper for .NET, targeting PostgreSQL first.

VeloORM aims for **Dapper-class performance with EF-class ergonomics**: code-first schema,
migrations, and type-safe LINQ on top of an execution model designed to be
**zero-allocation and Native-AOT-ready** on its hot paths, with no change tracking.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Table of contents

- [Architecture](#architecture)
- [Feature overview](#feature-overview)
- [Requirements](#requirements)
- [Installation](#installation)
- [Getting started](#getting-started)
- [Querying](#querying)
  - [LINQ operators](#linq-operators)
  - [Aggregates](#aggregates)
  - [Pagination](#pagination)
  - [Relationships and Include](#relationships-and-include)
  - [Many-to-many](#many-to-many)
  - [Explicit loading](#explicit-loading)
  - [Global query filters (soft delete)](#global-query-filters-soft-delete)
  - [Compiled queries](#compiled-queries)
  - [Bool-gated optional filters](#bool-gated-optional-filters)
  - [Raw SQL](#raw-sql)
- [Transactions](#transactions)
- [Bulk operations](#bulk-operations)
- [Logging](#logging)
- [Migrations and the CLI](#migrations-and-the-cli)
- [Database-first scaffolding](#database-first-scaffolding)
- [Repository layout](#repository-layout)
- [Performance](#performance)
- [Correctness and safety principles](#correctness-and-safety-principles)
- [Building and testing](#building-and-testing)
- [License](#license)
- [Azərbaycanca (Azerbaijani)](#azərbaycanca)

---

## Architecture

VeloORM uses a **three-layer hybrid execution model**. Every query is correct through layer 1;
layers 2 and 3 are pure, opt-in performance enhancements. When the compile-time layers are in any
doubt they emit nothing and the query falls through to the runtime engine, so the SQL is never wrong.

1. **Runtime engine (default).** `IQueryable` is translated to an internal query model and then to
   PostgreSQL SQL. The rendered SQL and a compiled materializer are cached in a shape-keyed cache
   (keyed by query structure, never by values), so re-running the same shape with different values
   reuses the compiled artifacts.
2. **Interceptor layer (compile-time).** For statically-known queries, a Roslyn incremental source
   generator plus C# Interceptors bake the SQL into the assembly as a constant, together with a
   reflection-free materializer. Runtime overhead is approximately zero.
3. **Fragment generation (compile-time).** For boolean-gated optional filters, one SQL fragment per
   filter is prepared and only the active fragments are cheaply assembled at runtime, cached by an
   active-fragment bitmask (n fragments, not 2^n variants).

Both the interceptor and the runtime engine translate the **same internal query model** to SQL, so
there is a single SQL-building path.

---

## Feature overview

- Type-safe LINQ: `Where`, `Select`, `OrderBy` / `ThenBy`, `Skip` / `Take`, `Distinct`,
  `First` / `FirstOrDefault`, `Single` / `SingleOrDefault`, `Count`, `Any`, `Sum`, `Average`,
  `Min`, `Max`, plus string methods, `IN`, and null checks.
- Compile-time interception of statically-known query shapes (zero runtime translation).
- Parameterized compiled queries via `Query.Compile`, with true zero-boxing parameter binding
  (`NpgsqlParameter<T>`).
- Relationships: reference and collection navigations, `Include` / `ThenInclude` (multi-level),
  explicit `Join`, and many-to-many through an explicit junction table.
- Explicit (on-demand) loading of navigations without change tracking.
- Model-level global query filters (for example soft delete) with an opt-out.
- Struct-based, allocation-light transactions.
- High-throughput bulk insert and bulk update using PostgreSQL binary `COPY`.
- Static, zero-allocation SQL logging with structural parameter masking.
- Code-first migrations and a `velo` command-line tool.
- Database-first scaffolding.
- Multi-targets `net8.0`, `net9.0`, and `net10.0`.

---

## Requirements

- .NET SDK 8, 9, or 10.
- PostgreSQL 16 (other versions generally work; 16 is used in tests).
- Docker is used for integration tests (Testcontainers) and the sample `docker-compose` stack.

---

## Installation

Install the single `VeloORM` meta-package. It pulls in everything an application needs — the core
abstractions, the runtime engine, the PostgreSQL provider, and the source generator (as an analyzer):

```bash
dotnet add package VeloORM
```

The meta-package also registers the `VeloORM.Generated` namespace for C# interceptors automatically,
so the compile-time interceptor layer works with no extra configuration.

Install the `velo` command-line tool (migrations and scaffolding) separately, as a .NET tool:

```bash
dotnet tool install --global VeloORM.Cli
```

**Prefer explicit references?** You can reference the underlying packages directly instead of the
meta-package. In that case you must also opt into interceptors yourself:

```xml
<ItemGroup>
  <PackageReference Include="VeloORM.Postgres" Version="0.1.0" />
  <!-- The generator ships as an analyzer. -->
  <PackageReference Include="VeloORM.Generator" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);VeloORM.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

Individual packages: `VeloORM.Core`, `VeloORM.Runtime`, `VeloORM.Postgres`, `VeloORM.Generator`,
`VeloORM.Migrations`, `VeloORM.Scaffold`, and the `velo` command-line tool.

---

## Getting started

Define entities. Naming follows convention: a class maps to the snake_case pluralized table name
(`Product` to `products`), a property maps to its snake_case column (`UserId` to `user_id`), and a
property named `Id` or `<Type>Id` is the primary key. Attributes such as `[Table]`, `[Column]`,
`[Key]`, `[ForeignKey]`, and `[NotMapped]` override the conventions.

```csharp
using VeloORM.Metadata;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

Build the model and create a context:

```csharp
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

const string connectionString =
    "Host=localhost;Port=5432;Username=velo;Password=...;Database=veloorm";

var factory = new NpgsqlConnectionFactory(connectionString);
var model = VeloModel.Build([typeof(Product)]);

var db = new VeloDbContext(
    model,
    PostgresDialect.Instance,
    factory,
    new PostgresCommandExecutor(factory));
```

Query:

```csharp
var inStock = db.Set<Product>()
    .Where(p => p.InStock && p.Price >= 10m)
    .OrderBy(p => p.Price)
    .ToList();
```

---

## Querying

### LINQ operators

```csharp
var names = db.Set<Product>()
    .Where(p => p.Price > 50m)
    .OrderByDescending(p => p.Price)
    .Select(p => new { p.Id, p.Name })
    .ToList();

var one    = db.Set<Product>().FirstOrDefault(p => p.Id == 42);
var count  = db.Set<Product>().Count(p => p.InStock);
var any    = db.Set<Product>().Any(p => p.Price > 1000m);

var starts = db.Set<Product>().Where(p => p.Name.StartsWith("Ap")).ToList();
var some   = new[] { "Apple", "Cherry" };
var inList = db.Set<Product>().Where(p => some.Contains(p.Name)).ToList();
```

A whole-table query such as `db.Set<Product>().ToList()`, and statically-known operator chains such
as `db.Set<Product>().OrderBy(p => p.Price).Take(20).ToList()`, are intercepted at compile time and
execute baked SQL with no runtime translation.

### Aggregates

```csharp
int     total   = db.Set<Product>().Count();
decimal sum     = db.Set<Product>().Sum(p => p.Price);
decimal average = db.Set<Product>().Average(p => p.Price);
decimal min     = db.Set<Product>().Min(p => p.Price);
decimal max     = db.Set<Product>().Max(p => p.Price);
```

Aggregates run server-side (`SELECT count(*)`, `SELECT sum(price)`, and so on) and never pull rows
into memory. LINQ empty-sequence semantics are preserved.

### Pagination

```csharp
int page = 2, pageSize = 20;
var results = db.Set<Product>()
    .OrderBy(p => p.Price)
    .Skip(page * pageSize)
    .Take(pageSize)
    .ToList();
```

### Relationships and Include

```csharp
using VeloORM.Runtime;

// Reference navigation (LEFT JOIN + graph materialization)
var orders = db.Set<Order>().Include(o => o.User).ToList();

// Collection navigation (follow-up query)
var users = db.Set<User>().Include(u => u.Orders).ToList();

// Multi-level ThenInclude
var deep = db.Set<Order>()
    .Include(o => o.Items)
    .ThenInclude(i => i.Product)
    .ToList();

// Explicit join
var joined = db.Set<Order>()
    .Join(db.Set<User>(), o => o.UserId, u => u.Id, (o, u) => new { o.Id, Buyer = u.Name })
    .ToList();
```

### Many-to-many

Many-to-many relationships are configured explicitly through their junction table, so the generated
SQL is always correct: there is no convention guessing.

```csharp
public class Post { public int Id { get; set; } public ICollection<Tag>? Tags { get; set; } }
public class Tag  { public int Id { get; set; } public ICollection<Post>? Posts { get; set; } }

var model = VeloModel.Build([typeof(Post), typeof(Tag)], b =>
{
    b.Entity<Post>().HasManyToMany(p => p.Tags!,  "post_tags", "post_id", "tag_id");
    b.Entity<Tag>().HasManyToMany(t => t.Posts!, "post_tags", "tag_id", "post_id");
});

var posts = db.Set<Post>().Include(p => p.Tags).ToList();
```

### Explicit loading

Load a navigation on demand for an already-materialized entity. This is stateless: there is no
change tracking or identity map.

```csharp
var order = db.Set<Order>().First();

db.Entry(order).Reference(o => o.User).Load();
await db.Entry(order).Collection(o => o.Items).LoadAsync();
```

### Global query filters (soft delete)

Define a model-level filter that is applied automatically to every query for that entity. Because
the compile-time interceptor cannot see a runtime filter, mark the entity with `[VeloQueryFilter]`
so its queries defer to the runtime engine (which applies the filter). Use `IgnoreQueryFilters()`
to opt out for a specific query.

```csharp
using VeloORM.Metadata;
using VeloORM.Runtime;

[VeloQueryFilter]
public class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }
}

var model = VeloModel.Build([typeof(Widget)],
    b => b.Entity<Widget>().HasQueryFilter(w => !w.IsDeleted));

var active  = db.Set<Widget>().ToList();                       // excludes soft-deleted
var everyId = db.Set<Widget>().IgnoreQueryFilters().ToList();  // includes all
```

### Compiled queries

For repeated parameterized queries, `Query.Compile` bakes the SQL at compile time and binds the
lambda's typed parameters with no value-type boxing. Store the returned delegate and invoke it many
times.

```csharp
using Query = VeloORM.Runtime.Query;

var byMinPrice = Query.Compile<VeloDbContext, decimal, List<Product>>(
    (ctx, min) => ctx.Set<Product>().Where(p => p.Price >= min).ToList());

var cheap = byMinPrice(db, 10m);
var dear  = byMinPrice(db, 100m);
```

### Bool-gated optional filters

For search-style queries with optional filters, prepared SQL fragments are assembled cheaply at
runtime and cached by which filters are active (n fragments, not 2^n variants).

```csharp
string? name = "Ap";
decimal? min = 5m;

var results = db.FilteredQuery<Product>("SELECT id, name, price, in_stock, created_at FROM products")
    .AndWhere(name is not null, $"name LIKE {name + "%"}")
    .AndWhere(min.HasValue,     $"price >= {min}")
    .ToList();
```

### Raw SQL

The raw-SQL escape hatch takes an interpolated string; interpolated values become bound parameters
automatically, so manual concatenation of values cannot reach the SQL text.

```csharp
var id = 42;
var product = db.QuerySingleOrDefault<Product>($"SELECT * FROM products WHERE id = {id}");
var rows    = db.Execute($"UPDATE products SET in_stock = {true} WHERE id = {id}");
var total   = db.ExecuteScalar<long>($"SELECT count(*) FROM products");
```

---

## Transactions

Transactions are allocation-light struct wrappers. The asynchronous handle is a `readonly struct`
usable with `await using`; disposing it without committing rolls the transaction back.

```csharp
await using (var tx = await db.BeginTransactionAsync())
{
    await tx.ExecuteAsync($"INSERT INTO products (name, price, in_stock, created_at) " +
                          $"VALUES ({"New"}, {9.99m}, {true}, {DateTimeOffset.UtcNow})");
    await tx.CommitAsync();
}

// Synchronous, stack-only ref struct
using (var scope = db.BeginTransaction())
{
    // ... work on scope.Connection / scope.Transaction ...
    scope.Commit();
}
```

---

## Bulk operations

Bulk insert uses PostgreSQL binary `COPY`; bulk update stages rows into a temporary table and
applies a single `UPDATE ... FROM`. Both avoid per-row round trips.

```csharp
using VeloORM.Postgres;

var rows = Enumerable.Range(1, 100_000)
    .Select(i => new Product
    {
        Name = "P" + i, Price = i % 100, InStock = i % 2 == 0, CreatedAt = DateTimeOffset.UtcNow,
    })
    .ToList();

db.BulkInsert(rows);

// Load, modify, then bulk-update by key
var toUpdate = db.Set<Product>().ToList();
foreach (var p in toUpdate) p.Price += 1m;
db.BulkUpdate(toUpdate);

// Transactional bulk update
await using var tx = await db.BeginTransactionAsync();
db.BulkUpdate(toUpdate, tx);
await tx.CommitAsync();
```

---

## Logging

A single delegate is stored on the context; it is invoked once per command with the parameterized
SQL. Values are always bound (`$N` placeholders), so they never appear in the logged text: masking
is structural.

```csharp
db.LogTo(Console.WriteLine);
```

---

## Migrations and the CLI

VeloORM is code-first. The `velo` command-line tool (a .NET tool) manages migrations and scaffolding.

```bash
# Install the tool
dotnet tool install --global VeloORM.Cli

# Create a migration from the current model
velo add-migration InitialCreate --connection "Host=...;Database=veloorm"

# Apply pending migrations
velo update-database --connection "Host=...;Database=veloorm"

# List and revert
velo list-migrations
velo revert
```

The connection string can also be supplied through the `VELO_CONNECTION` environment variable.

---

## Database-first scaffolding

Reverse-engineer entity classes and a context from an existing database:

```bash
velo scaffold --connection "Host=...;Database=veloorm" --namespace MyApp.Data --context AppContext
```

---

## Repository layout

```
src/
  VeloORM.Core         core abstractions, query model (AST), metadata
  VeloORM.Runtime      runtime IQueryable provider + shape-keyed cache
  VeloORM.Generator    Roslyn source generator + interceptors (netstandard2.0)
  VeloORM.Postgres     Npgsql dialect, type mapping, COPY bulk insert/update
  VeloORM.Migrations   migration model, history, diff engine
  VeloORM.Scaffold     database-first reverse engineering
  VeloORM.Cli          the `velo` command-line tool (.NET tool)
tests/
  VeloORM.Tests.Unit
  VeloORM.Tests.Integration   (Testcontainers + real PostgreSQL)
samples/
  VeloORM.SampleApi           minimal ASP.NET Core Web API
benchmarks/
  VeloORM.Benchmarks          ADO.NET / Dapper / EF Core / VeloORM comparison
docker/
  docker-compose.yml          PostgreSQL 16 + Adminer
```

---

## Performance

Benchmarks (BenchmarkDotNet, .NET 8, PostgreSQL 16) compare raw ADO.NET, Dapper, EF Core, and
VeloORM across all query shapes. Representative results (lower is better):

| Scenario                    | ADO.NET      | Dapper       | EF Core       | VeloORM       |
|-----------------------------|--------------|--------------|---------------|---------------|
| Select all, 100k (time)     | 76.1 ms      | 92.7 ms      | 96.7 ms       | 81.3 ms       |
| Select all, 100k (alloc)    | 12.4 MB      | 22.8 MB      | 31.7 MB       | 12.4 MB       |
| Count, 100k (alloc)         | 103 B        | 82 B         | 48,975 B      | 770 B         |
| Reference include, 100k     | 79.8 ms      | 103.6 ms     | 206.9 ms      | 102.6 ms      |
| Many-to-many include        | 10.6 ms      | 11.7 ms      | 27.3 ms       | 12.7 ms       |
| Bulk insert, 20k rows       | 116 ms       | 99,380 ms    | 1,819 ms      | 110 ms        |
| Bulk update, 20k rows       | 283.9 ms     | 25,282 ms    | 835 ms        | 133.8 ms      |

Summary: VeloORM is at or near ADO.NET and Dapper on time, is generally 2x to 16x faster than EF
Core, and allocates far less than EF Core (often 10x to 500x less). Bulk operations, backed by
`COPY` and temporary tables, match hand-written ADO.NET and outpace the alternatives by orders of
magnitude.

Run the benchmarks (with PostgreSQL up):

```bash
cd benchmarks/VeloORM.Benchmarks
dotnet run -c Release                            # all
dotnet run -c Release -- --filter '*Aggregate*'  # a single group
```

---

## Correctness and safety principles

- **Correctness is non-negotiable.** When the compile-time generator is in any doubt, it emits
  nothing and the query runs through the runtime engine. The SQL is never wrong.
- **SQL injection is structurally impossible.** All values are always bound parameters; only
  compile-time-known identifiers (table and column names) are ever concatenated into SQL. The raw
  SQL escape hatch uses an interpolated-string handler that turns interpolation holes into bound
  parameters.
- **No change tracking.** VeloORM has no identity map or unit of work; writes go through bulk
  operations or raw SQL.

---

## Building and testing

```bash
# Start PostgreSQL and Adminer
docker compose -f docker/docker-compose.yml up -d

# Build (multi-targets net8.0 / net9.0 / net10.0)
dotnet build VeloORM.slnx

# Unit tests (no database)
dotnet test tests/VeloORM.Tests.Unit

# Integration tests (Testcontainers PostgreSQL 16; requires Docker)
dotnet test tests/VeloORM.Tests.Integration
```

---

## License

MIT. See [LICENSE](LICENSE).

---
---

# Azərbaycanca

VeloORM — .NET üçün yüksək performanslı, hibrid Obyekt-Relyasion Xəritələyicidir (ORM); ilk hədəf
PostgreSQL-dir.

Məqsəd: **Dapper səviyyəsində performans, EF səviyyəsində rahatlıq** — code-first sxem, miqrasiyalar
və tip-təhlükəsiz LINQ. İcra modeli hot-path-larda **sıfır-allokasiya və Native-AOT-a hazır** olacaq
şəkildə qurulub və obyekt vəziyyəti izləməsi (change tracking) yoxdur.

## Arxitektura

VeloORM **üç-laylı hibrid icra modeli** işlədir. Hər sorğu 1-ci lay vasitəsilə həmişə düzgündür;
2-ci və 3-cü laylar yalnız performans üçün, könüllü əlavələrdir. Compile-time layları hər hansı
şübhə olduqda heç nə emit etmir və sorğu runtime mühərrikinə düşür, beləliklə SQL heç vaxt səhv olmur.

1. **Runtime mühərriki (default).** `IQueryable` daxili sorğu modelinə, sonra PostgreSQL SQL-ə
   çevrilir. Hazırlanmış SQL və kompilyasiya olunmuş materializer struktur-açarlı keşdə saxlanır
   (açar sorğunun strukturudur, dəyərlər deyil), beləliklə eyni struktur fərqli dəyərlərlə yenidən
   işlədildikdə hazır artefaktlar təkrar istifadə olunur.
2. **Interceptor layı (compile-time).** Statik olaraq məlum sorğular üçün Roslyn generatoru və C#
   Interceptor-ları SQL-i assembliyə const kimi, reflection-suz materializer ilə birlikdə
   yerləşdirir. Runtime yükü təxminən sıfırdır.
3. **Fragment generasiyası (compile-time).** Boolean-şərtli opsional filtrlər üçün hər filtrə bir
   SQL fraqmenti hazırlanır və yalnız aktiv fraqmentlər runtime-da ucuz şəkildə birləşdirilir;
   nəticə aktiv-fraqment bitmask-ı ilə keşlənir (n fraqment, 2^n variant yox).

Həm interceptor, həm də runtime mühərriki **eyni daxili sorğu modelini** SQL-ə çevirir — vahid
SQL-qurma yolu var.

## Əsas imkanlar

- Tip-təhlükəsiz LINQ: `Where`, `Select`, `OrderBy`/`ThenBy`, `Skip`/`Take`, `Distinct`,
  `First`/`FirstOrDefault`, `Single`/`SingleOrDefault`, `Count`, `Any`, `Sum`, `Average`, `Min`,
  `Max`, həmçinin string metodları, `IN` və null yoxlamaları.
- Statik məlum sorğuların compile-time interception-u (sıfır runtime tərcüməsi).
- `Query.Compile` ilə parametrli kompilyasiya olunmuş sorğular — həqiqi sıfır-boxing parametr
  binding (`NpgsqlParameter<T>`).
- Əlaqələr: reference və collection naviqasiyalar, `Include`/`ThenInclude` (çox-səviyyəli), açıq
  `Join` və junction cədvəli ilə many-to-many.
- Naviqasiyaların açıq (on-demand) yüklənməsi — change tracking olmadan.
- Model səviyyəsində qlobal sorğu filtrləri (məsələn soft delete) və deaktiv etmə imkanı.
- Struct əsaslı, yüngül tranzaksiyalar.
- PostgreSQL binary `COPY` ilə yüksək sürətli bulk insert və bulk update.
- Statik, sıfır-allokasiya SQL loglama və struktural parametr maskalama.
- Code-first miqrasiyalar və `velo` komanda-sətri aləti.
- Database-first scaffolding.
- `net8.0`, `net9.0` və `net10.0` dəstəyi.

## Tələblər

- .NET SDK 8, 9 və ya 10.
- PostgreSQL 16 (digər versiyalar da işləyir; testlərdə 16 istifadə olunur).
- İnteqrasiya testləri (Testcontainers) və nümunə `docker-compose` üçün Docker.

## Quraşdırma

Tək `VeloORM` meta-paketini quraşdırın. O, tətbiqin ehtiyac duyduğu hər şeyi gətirir — core
abstraksiyaları, runtime mühərriki, PostgreSQL provayderi və source generator (analizator kimi):

```bash
dotnet add package VeloORM
```

Meta-paket həmçinin C# interceptor-lar üçün `VeloORM.Generated` namespace-ni avtomatik qeydə alır,
ona görə compile-time interceptor layı əlavə konfiqurasiya olmadan işləyir.

`velo` komanda-sətri alətini (miqrasiyalar və scaffolding) ayrıca, .NET tool kimi quraşdırın:

```bash
dotnet tool install --global VeloORM.Cli
```

**Açıq referensləri üstün tutursunuz?** Meta-paket əvəzinə alt paketləri birbaşa referens edə
bilərsiniz. Bu halda interceptor-ları özünüz aktivləşdirməlisiniz:

```xml
<ItemGroup>
  <PackageReference Include="VeloORM.Postgres" Version="0.1.0" />
  <PackageReference Include="VeloORM.Generator" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);VeloORM.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

Ayrı-ayrı paketlər: `VeloORM.Core`, `VeloORM.Runtime`, `VeloORM.Postgres`, `VeloORM.Generator`,
`VeloORM.Migrations`, `VeloORM.Scaffold` və `velo` komanda-sətri aləti.

## Başlanğıc

Entity təyin edin. Adlandırma konvensiyaya əsaslanır: sinif snake_case cəm cədvəl adına (`Product`
→ `products`), property snake_case sütuna (`UserId` → `user_id`) uyğunlaşır; `Id` və ya `<Tip>Id`
adlı property ilkin açardır. `[Table]`, `[Column]`, `[Key]`, `[ForeignKey]`, `[NotMapped]`
atributları konvensiyanı üstələyir.

```csharp
using VeloORM.Metadata;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

Model qurun və kontekst yaradın:

```csharp
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;

const string connectionString =
    "Host=localhost;Port=5432;Username=velo;Password=...;Database=veloorm";

var factory = new NpgsqlConnectionFactory(connectionString);
var model = VeloModel.Build([typeof(Product)]);

var db = new VeloDbContext(
    model,
    PostgresDialect.Instance,
    factory,
    new PostgresCommandExecutor(factory));
```

Sorğu:

```csharp
var inStock = db.Set<Product>()
    .Where(p => p.InStock && p.Price >= 10m)
    .OrderBy(p => p.Price)
    .ToList();
```

## Sorğular

### LINQ operatorları

```csharp
var names = db.Set<Product>()
    .Where(p => p.Price > 50m)
    .OrderByDescending(p => p.Price)
    .Select(p => new { p.Id, p.Name })
    .ToList();

var one    = db.Set<Product>().FirstOrDefault(p => p.Id == 42);
var count  = db.Set<Product>().Count(p => p.InStock);
var starts = db.Set<Product>().Where(p => p.Name.StartsWith("Ap")).ToList();
```

`db.Set<Product>().ToList()` kimi bütün-cədvəl sorğusu və `OrderBy(...).Take(20)` kimi statik məlum
operator zəncirləri compile-time-da intercept olunur və runtime tərcüməsi olmadan hazır SQL icra
edir.

### Aqreqatlar

```csharp
int     total   = db.Set<Product>().Count();
decimal sum     = db.Set<Product>().Sum(p => p.Price);
decimal average = db.Set<Product>().Average(p => p.Price);
```

Aqreqatlar server tərəfində icra olunur (`SELECT count(*)`, `SELECT sum(price)` və s.) və sətirləri
yaddaşa çəkmir.

### Paginasiya

```csharp
int page = 2, pageSize = 20;
var results = db.Set<Product>()
    .OrderBy(p => p.Price)
    .Skip(page * pageSize)
    .Take(pageSize)
    .ToList();
```

### Əlaqələr və Include

```csharp
using VeloORM.Runtime;

var orders = db.Set<Order>().Include(o => o.User).ToList();
var users  = db.Set<User>().Include(u => u.Orders).ToList();

var deep = db.Set<Order>()
    .Include(o => o.Items)
    .ThenInclude(i => i.Product)
    .ToList();
```

### Many-to-many

Many-to-many əlaqələri junction cədvəli ilə açıq konfiqurasiya olunur, beləliklə hasil olunan SQL
həmişə düzgündür: konvensiya təxmini yoxdur.

```csharp
var model = VeloModel.Build([typeof(Post), typeof(Tag)], b =>
{
    b.Entity<Post>().HasManyToMany(p => p.Tags!, "post_tags", "post_id", "tag_id");
    b.Entity<Tag>().HasManyToMany(t => t.Posts!, "post_tags", "tag_id", "post_id");
});

var posts = db.Set<Post>().Include(p => p.Tags).ToList();
```

### Açıq yükləmə (Explicit loading)

Artıq materializə olunmuş entity üçün naviqasiyanı tələb olunduqda yükləyin. Bu statelessdir:
change tracking və identity map yoxdur.

```csharp
var order = db.Set<Order>().First();
db.Entry(order).Reference(o => o.User).Load();
await db.Entry(order).Collection(o => o.Items).LoadAsync();
```

### Qlobal sorğu filtrləri (soft delete)

Model səviyyəsində filtr təyin edin; həmin entity üçün hər sorğuya avtomatik tətbiq olunur.
Interceptor runtime filtrini görə bilmədiyi üçün entity-ni `[VeloQueryFilter]` ilə işarələyin ki,
sorğular runtime mühərrikinə düşsün (filtri o tətbiq edir). Konkret sorğu üçün
`IgnoreQueryFilters()` ilə deaktiv edin.

```csharp
[VeloQueryFilter]
public class Widget
{
    public int Id { get; set; }
    public bool IsDeleted { get; set; }
}

var model = VeloModel.Build([typeof(Widget)],
    b => b.Entity<Widget>().HasQueryFilter(w => !w.IsDeleted));

var active  = db.Set<Widget>().ToList();                       // soft-deleted istisna
var everyId = db.Set<Widget>().IgnoreQueryFilters().ToList();  // hamısı
```

### Kompilyasiya olunmuş sorğular

Təkrarlanan parametrli sorğular üçün `Query.Compile` SQL-i compile-time-da hazırlayır və lambda-nın
tipli parametrlərini dəyər-boxing olmadan bağlayır. Qaytarılan delegate-i saxlayıb dəfələrlə çağırın.

```csharp
using Query = VeloORM.Runtime.Query;

var byMinPrice = Query.Compile<VeloDbContext, decimal, List<Product>>(
    (ctx, min) => ctx.Set<Product>().Where(p => p.Price >= min).ToList());

var cheap = byMinPrice(db, 10m);
```

### Boolean-şərtli opsional filtrlər

Opsional filtrli axtarış sorğuları üçün hazırlanmış SQL fraqmentləri runtime-da ucuz birləşdirilir və
hansı filtrlərin aktiv olmasına görə keşlənir (n fraqment, 2^n variant yox).

```csharp
var results = db.FilteredQuery<Product>("SELECT id, name, price, in_stock, created_at FROM products")
    .AndWhere(name is not null, $"name LIKE {name + "%"}")
    .AndWhere(min.HasValue,     $"price >= {min}")
    .ToList();
```

### Xam SQL

Xam SQL çıxışı interpolyasiya olunmuş string qəbul edir; interpolyasiya dəyərləri avtomatik olaraq
bağlı parametrlərə çevrilir, ona görə dəyərləri əl ilə birləşdirmək SQL mətninə çata bilmir.

```csharp
var id = 42;
var product = db.QuerySingleOrDefault<Product>($"SELECT * FROM products WHERE id = {id}");
var total   = db.ExecuteScalar<long>($"SELECT count(*) FROM products");
```

## Tranzaksiyalar

Tranzaksiyalar yüngül struct sarğılardır. Asinxron handle `readonly struct`-dır, `await using` ilə
işlədilir; commit olunmadan dispose edilərsə tranzaksiya geri qaytarılır (rollback).

```csharp
await using (var tx = await db.BeginTransactionAsync())
{
    await tx.ExecuteAsync($"UPDATE products SET in_stock = {false} WHERE id = {1}");
    await tx.CommitAsync();
}

using (var scope = db.BeginTransaction())
{
    scope.Commit();
}
```

## Bulk əməliyyatlar

Bulk insert PostgreSQL binary `COPY`-dən istifadə edir; bulk update sətirləri müvəqqəti cədvələ
yerləşdirib tək `UPDATE ... FROM` icra edir. Hər ikisi sətir-sətir gediş-gəlişdən qaçır.

```csharp
using VeloORM.Postgres;

db.BulkInsert(rows);

var toUpdate = db.Set<Product>().ToList();
foreach (var p in toUpdate) p.Price += 1m;
db.BulkUpdate(toUpdate);
```

## Loglama

Kontekstdə tək bir delegate saxlanır; hər komanda üçün bir dəfə parametrli SQL ilə çağırılır.
Dəyərlər həmişə bağlıdır (`$N`), ona görə log mətnində görünmür: maskalama strukturaldır.

```csharp
db.LogTo(Console.WriteLine);
```

## Miqrasiyalar və CLI

VeloORM code-first-dir. `velo` aləti (bir .NET tool) miqrasiya və scaffolding-i idarə edir.

```bash
dotnet tool install --global VeloORM.Cli
velo add-migration InitialCreate --connection "Host=...;Database=veloorm"
velo update-database --connection "Host=...;Database=veloorm"
velo list-migrations
velo revert
```

Connection string həmçinin `VELO_CONNECTION` mühit dəyişəni ilə verilə bilər.

## Database-first scaffolding

Mövcud verilənlər bazasından entity siniflərini və konteksti geri-mühəndislik edin:

```bash
velo scaffold --connection "Host=...;Database=veloorm" --namespace MyApp.Data --context AppContext
```

## Performans

Benchmark-lar (BenchmarkDotNet, .NET 8, PostgreSQL 16) raw ADO.NET, Dapper, EF Core və VeloORM-u
bütün sorğu növləri üzrə müqayisə edir. Nümunə nəticələr (az = yaxşı):

| Ssenari                    | ADO.NET      | Dapper       | EF Core       | VeloORM       |
|----------------------------|--------------|--------------|---------------|---------------|
| Select all, 100k (vaxt)    | 76.1 ms      | 92.7 ms      | 96.7 ms       | 81.3 ms       |
| Count, 100k (RAM)          | 103 B        | 82 B         | 48,975 B      | 770 B         |
| Reference include, 100k    | 79.8 ms      | 103.6 ms     | 206.9 ms      | 102.6 ms      |
| Bulk insert, 20k sətir     | 116 ms       | 99,380 ms    | 1,819 ms      | 110 ms        |
| Bulk update, 20k sətir     | 283.9 ms     | 25,282 ms    | 835 ms        | 133.8 ms      |

Xülasə: VeloORM vaxt baxımından ADO.NET və Dapper səviyyəsindədir, EF Core-dan adətən 2–16x
sürətlidir və EF Core-dan xeyli az yaddaş ayırır (çox vaxt 10–500x az). `COPY` və müvəqqəti
cədvəllərə əsaslanan bulk əməliyyatlar raw ADO.NET ilə bərabərdir və alternativləri
onlarla-yüzlərlə dəfə üstələyir.

```bash
cd benchmarks/VeloORM.Benchmarks
dotnet run -c Release
```

## Düzgünlük və təhlükəsizlik prinsipləri

- **Düzgünlük güzəştsizdir.** Compile-time generator hər hansı şübhə olduqda heç nə emit etmir və
  sorğu runtime mühərriki ilə işləyir. SQL heç vaxt səhv olmur.
- **SQL injection struktural olaraq mümkünsüzdür.** Bütün dəyərlər həmişə bağlı parametrlərdir;
  yalnız compile-time-da məlum identifikatorlar (cədvəl/sütun adları) SQL-ə birləşdirilir.
- **Change tracking yoxdur.** Yazma bulk əməliyyatlar və ya xam SQL ilə aparılır.

## Qurma və test

```bash
docker compose -f docker/docker-compose.yml up -d
dotnet build VeloORM.slnx
dotnet test tests/VeloORM.Tests.Unit
dotnet test tests/VeloORM.Tests.Integration
```

## Lisenziya

MIT. Bax: [LICENSE](LICENSE).
