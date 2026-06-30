# CLAUDE.md — VeloORM

This file is the working contract for building **VeloORM**. Read it at the start
of every session. Update the phase checklist as work completes.

## Project name & purpose

**VeloORM** is a new, open-source, high-performance hybrid ORM for C#/.NET,
targeting PostgreSQL first. Goal: **Dapper-class performance with EF-class
ergonomics** — code-first schema, migrations, and type-safe LINQ. The name
`VeloORM` is fixed and used for every namespace, assembly, and package id.

## Hybrid architecture (three layers)

Layers are listed in order of fallback priority. Every query is **correct**
through layer 1; layers 2 and 3 are pure progressive enhancement.

1. **Runtime engine (default foundation).** `IQueryable` → internal query model
   → PostgreSQL SQL translator. Results (SQL text + parameter binding plan +
   materializer delegate) are cached in a **shape-keyed** `ConcurrentDictionary`,
   where the key is derived from the query *structure*, never from values. Every
   query must work through this path even if the generator emits nothing.
2. **Interceptor layer (compile-time).** For queries whose shape is statically
   known, a Roslyn incremental source generator + C# Interceptors generate SQL at
   compile time and bake it into the assembly (`const`), with a compile-time
   (reflection-free) materializer. Runtime overhead ≈ zero. This is an override
   layered on top of the runtime engine via `[InterceptsLocation]`.
3. **Fragment generation (compile-time fragment + cheap runtime assembly).** For
   `if (flag) q = q.Where(...)` bool-gated optional filters, one SQL fragment per
   filter is generated at compile time; at runtime only the active fragments are
   concatenated cheaply (StringBuilder / `ArrayPool`) and `$N` ordinals are
   numbered. No `2^n` variant explosion — `n` fragments, assembled-SQL cached by
   active-fragment bitmask.

### Correctness principle (NON-NEGOTIABLE)
When the generator is in **any** doubt, it emits **nothing** → the query falls
through to the runtime engine. **Never emit wrong SQL.** Performance may vary;
correctness never does.

### Injection rule (STRUCTURAL)
SQL injection must be structurally impossible. **All values are always bound
parameters.** Only **identifiers** (compile-time known: table/column names) are
ever concatenated into SQL strings. Raw SQL is only writable via the
`[InterpolatedStringHandler]` escape hatch, which turns interpolation holes into
bound parameters automatically — manual string concatenation of values is not
possible through the public API.

## Repository structure

```
src/VeloORM.Core         abstractions: IDbContext, ISqlDialect, query model (AST), materializer interfaces
src/VeloORM.Runtime      runtime engine: IQueryable provider, expression→SQL, shape-keyed cache
src/VeloORM.Generator    Roslyn incremental source generator + interceptors (netstandard2.0 analyzer)
src/VeloORM.Postgres     Npgsql dialect, .NET↔Postgres type mapping, COPY bulk insert
src/VeloORM.Migrations   migration model, history (__velo_migrations_history), diff engine
src/VeloORM.Scaffold     DB-first reverse engineering
src/VeloORM.Cli          `velo` CLI: add-migration, update-database, scaffold, revert, list-migrations
tests/VeloORM.Tests.Unit
tests/VeloORM.Tests.Integration   Testcontainers vs real Postgres
samples/VeloORM.SampleApi         minimal ASP.NET Core Web API
docker/docker-compose.yml         Postgres 16 + Adminer
```

## Coding rules

- **Allocation-aware hot paths.** Prefer `ArrayPool<T>`, `ValueTask`, struct
  enumerators, and `Span<T>`. Avoid boxing. The materializer must be
  reflection-free in the generated path; the runtime fallback may use
  `Expression.Compile`/emit but caches the result.
- **AOT / trimming.** Compatibility is a target. The generated path must avoid
  reflection so the library can be trimmed/AOT-compiled.
- **Dialect abstraction.** All dialect-specific behavior goes behind
  `ISqlDialect` (parameter prefix `$N`, `LIMIT/OFFSET`, identifier quoting, type
  mapping, upsert syntax). Only PostgreSQL is fully implemented in v1, but no
  Postgres-isms leak outside `VeloORM.Postgres`. MySQL / SQL Server must be
  addable later without touching Core/Runtime.
- **One query model, two translators.** Both the interceptor and the runtime
  engine translate the *same* internal query model (AST) to SQL — no duplicated
  SQL-building logic.
- **Injection rule** (above) is enforced structurally, not by convention.

## Test strategy

- **Unit** (`VeloORM.Tests.Unit`): pure logic — metadata model, query-model→SQL
  building, dialect behavior. No database.
- **Integration** (`VeloORM.Tests.Integration`): real PostgreSQL via
  **Testcontainers.PostgreSql** (Postgres 16). Round-trips, LINQ correctness,
  cache behavior, migrations, scaffolding.
- A query's interceptor/fragment/runtime results must be **identical**; tests
  assert equivalence across layers and that the hot path does no redundant work
  (e.g. warm cache does not re-translate; interceptor path allocates no
  expression trees).

## Key decisions (record new ones here)

- Runtime libraries multi-target **`net8.0;net10.0`**; the generator/analyzer is
  **`netstandard2.0`** (Roslyn requirement); the CLI is **`net10.0`**.
- Solution uses the modern **`.slnx`** format (`VeloORM.slnx`), produced by the
  .NET 10 SDK. Build/test with `dotnet build VeloORM.slnx` / `dotnet test`.
- Interceptors enabled via the MSBuild **`<InterceptorsNamespaces>`** property
  (modern replacement for `<Features>InterceptorsPreview</Features>`); generated
  interceptors live in the **`VeloORM.Generated`** namespace. Wired up in Phase 5.
- Npgsql **8.0.x** (works on both `net8.0` and `net10.0`); Postgres image **16**.
- When a decision is uncertain: pick the simplest working option, record it here,
  continue.
- **Shape-keyed cache (Phase 3):** the cache stores the rendered SQL string + the compiled
  materializer delegate, keyed by `ShapeKey` (structure only — parameters contribute their CLR
  type, never their value). `QueryCompilationCount` counts cache misses; it stays constant when a
  shape is re-run with different values. The lightweight expression→model build + value extraction
  runs per call (values change); the expensive artifacts (SQL render, `Expression.Compile`
  materializer) are cached. Parameters carry a translator-assigned `Ordinal` so binding order ==
  placeholder order, letting hits reuse SQL without re-rendering.
- **Runtime engine is the reflection/Expression.Compile fallback** (trim analyzer disabled on
  `VeloORM.Runtime`); `[RequiresUnreferencedCode]` on `VeloDbContext`/`VeloModel.Build` warns
  consumers. The trim/AOT-safe path is the source generator (Phase 5).

## Phase checklist

Execution order. After each phase: build, run tests, tick the box, write a brief
"PHASE N COMPLETE" report, and commit.

> **Current scope:** Phases 0–13 complete. **Active block: Phase 14–17 (Priority 1 —
> high-performance core), then stop for review.** Phases 18–21 (Priority 2 — enterprise
> relational features) follow only on explicit "continue".

- [x] **Phase 0** — Skeleton & infrastructure (solution, projects, Directory.Build.props, docker-compose, README/LICENSE/CLAUDE.md). Compiles; `docker compose up -d` works. ✅
- [x] **Phase 1** — Core abstractions (IDbContext, DbSet<T>, metadata model, ISqlDialect, query model AST, IMaterializer). Unit tests pass (19 tests). ✅
- [x] **Phase 2** — Postgres dialect + manual materializer (Npgsql, PostgresDialect, type mapping). Integration round-trip test passes (3 tests). ✅
- [x] **Phase 3** — Runtime engine ⭐ (IQueryable provider, expression→SQL, bound params, shape-keyed cache). Integration tests pass (15 incl. warm-cache no-recompile). ✅ *Operators: Where/Select/OrderBy/ThenBy/Take/Skip/First/Single/Any/Count + string methods, IN, null checks. **Relationships implemented:** explicit `Join` (INNER JOIN + projection), reference-navigation member access auto-joins (`o.User.Name` in Where/OrderBy/Select), `Include(o => o.User)` (LEFT JOIN + entity-graph materialization), `Include(u => u.Orders)` (collection via follow-up `WHERE fk = ANY($1)` query). **Multi-level `ThenInclude` implemented:** reference→reference (any depth, via nested `LEFT JOIN`s + recursive entity-graph materialization), collection→reference (the follow-up query `LEFT JOIN`s the child reference), collection→collection (further follow-up queries). Navigation/FK metadata resolved by convention (`{Nav}Id` / target back-reference) + `[ForeignKey]`. GroupBy / GroupJoin / SelectMany / many-to-many remain `NotSupportedException`/future; reference→collection `ThenInclude` bails (correctness).*
- [x] **Phase 4** — Raw SQL escape hatch + `[InterpolatedStringHandler]` (`VeloInterpolatedSql`; `Query`/`QueryAsync`/`QuerySingleOrDefault`/`Execute`/`ExecuteAsync`/`ExecuteScalar`). 6 integration tests (function + view + injection). ✅
- [x] **Phase 5** — Source generator: interceptor layer. Incremental generator intercepts static `db.Set<T>().ToList()/First()/Single()/Count()/Any()` (no predicate/operators) via `GetInterceptableLocation()` + `[InterceptsLocation]`, baking compile-time SQL + reflection-free materializer; verified zero runtime translation. ✅ *Predicate/operator chains bail to runtime (correctness principle); compile-time predicate translation is the next enhancement.*
- [x] **Phase 6** — Fragment generation (bool-gated optional filters). `FilteredQuery<T>().AndWhere(cond, $"...")` with conditional `VeloFragment` handler; only active fragments assembled, `$N` renumbered, assembled SQL cached by active-fragment bitmask (n fragments, not 2ⁿ). 5 tests incl. bitmask-cache + injection. ✅ *Runtime fragment engine; Roslyn auto-detection of the `if(flag) q=q.Where(...)` source pattern is a future enhancement.*
- [x] **Phase 7** — Diagnostics + `Query.Compile`. Generator emits **VELO001** (info) when a query rooted at `Set<T>()` falls to runtime translation, and **VELO002** (error) when `Query.Compile`'s argument isn't a query rooted at the context's `Set<T>()`. `Query.Compile` overloads return reusable delegates (compiled-query handle). 4 generator-driver unit tests. ✅ *Internal runtime query namespace renamed `VeloORM.Runtime.Query` → `VeloORM.Runtime.Internal` to free the `Query` type name.*
- [x] **Phase 8** — Code-first schema + migrations. `ModelSchemaBuilder` (VeloModel→schema), `PostgresSchemaReader` (information_schema/pg catalog), `SchemaDiffer` (tables/columns/indexes/PK), `PostgresMigrationSqlGenerator` (DDL up/down), `MigrationScaffolder` (Up/Down via reverse diff), `Migrator` (transactional apply/revert + `__velo_migrations_history`), `MigrationFileStore`. Integration tests (create→add column+unique index→revert round-trip, no-op detection, **FK create+revert round-trip**). ✅ **FK diffing implemented:** reference navigations become `SchemaForeignKey`s; `AddForeignKeyOperation`/`DropForeignKeyOperation` rendered as `ALTER TABLE … ADD/DROP CONSTRAINT` (add ops ordered after all `CREATE TABLE`s; drop ops before table drops). FKs read from `information_schema` and compared by name (like indexes).*
- [x] **Phase 9** — DB-first scaffolding. `ScaffoldTypeMapper` (store→CLR), `EntityScaffolder` reverse-engineers entity classes (snake_case→PascalCase, singularized class names, `[Table]`/`[Column]` only when differing from convention, `[Key]` on PK) + a context with `IQueryable<T>` properties from a live DB. 2 integration tests. ✅
- [x] **Phase 10** — CLI (`velo`) as a dotnet tool. `CliCommands` (add-migration/update-database/revert/list-migrations/scaffold) over the Migrations/Scaffold engines; `Program` arg dispatcher, connection resolution (`--connection`/`VELO_CONNECTION`), assembly loading for add-migration. `PackAsTool`/`ToolCommandName=velo`. 2 integration tests (full migration lifecycle + scaffold). ✅
- [x] **Phase 11** — Bulk (`COPY`) & performance. `PostgresBulkInserter` (binary COPY, skips identity columns) — 1000-row COPY integration test. `benchmarks/VeloORM.Benchmarks` (BenchmarkDotNet) compares VeloORM vs Dapper vs EF Core on select; compiles, run manually against a DB (`dotnet run -c Release`). ✅
- [x] **Phase 12** — SampleApi demonstrating all three layers. Minimal ASP.NET Core API with code-first auto-migrate on startup; endpoints for interceptor (`GET /products`), runtime (`GET /products/{id}`), fragment (`GET /products/search`), raw SQL (`GET /products/expensive`, `POST /products`), and `GET /health`. Built-in OpenAPI at `/openapi/v1.json`. Verified end-to-end against compose Postgres (all endpoints return correct data). ✅
- [x] **Phase 13** — NuGet packaging. All 7 shipping projects pack (IsPackable default flipped; tests/sample/benchmarks opt out), README + LICENSE + `.snupkg` symbols included, deterministic build. Generator ships as an analyzer (`BuildOutputTargetFolder=analyzers/dotnet/cs`, no runtime deps). `eng/pack.ps1` + GitHub Actions CI (`.github/workflows/ci.yml`: build/test/pack with Postgres service; publish gated on secret). `dotnet pack VeloORM.slnx` → 7 nupkg + 7 snupkg, exit 0. ✅

---

## High-performance & relational expansion (Priority 1 + 2)

> Goal: **Zero-allocation / Native-AOT-ready** generated path with EF-class ergonomics;
> no change-tracking. Generated (compile-time) path must be boxing-free and reflection-free;
> when in doubt the generator emits **nothing** → runtime fallback (correctness principle).
> Execution: **Phase 14–17 as one block, then stop for review.** Phase 18–21 follow on "continue".

### PRIORITY 1 — high-performance core (active block)

- [x] **Phase 14** — Multi-targeting + `InterceptsLocation` polyfill. Runtime libs (Core, Runtime, Postgres, Migrations, Scaffold) multi-target **`net8.0;net9.0;net10.0`**; generator stays `netstandard2.0`, CLI stays `net10.0`. Generator-emitted `file`-scoped `System.Runtime.CompilerServices.InterceptsLocationAttribute(int version, string data)` polyfill verified to compile on all three TFMs (BCL does not ship it for the interceptors feature). Build green on net8/9/10. ✅
- [x] **Phase 15a** — Runtime scalar aggregates. `Sum/Average/Min/Max` added end-to-end to the runtime engine (`QueryTerminal` + `SqlBuilder.BuildAggregate` + `ExpressionTranslator.ApplyAggregate` + `QueryEngine.ExecuteAggregate`) with LINQ empty-sequence semantics (Sum→0; Min/Max/Average→null if nullable else throw). This makes the runtime fallback correct for these operators (were `NotSupportedException`). 4 unit + 2 integration tests. ✅
- [x] **Phase 15b** — Compile-time interception of static operator chains ⭐. New `SymbolQueryTranslator` (Roslyn) bakes SQL for `Set<T>()`-rooted chains whose SQL is **fully static** (no closure value to capture — a parameterless interceptor signature cannot receive one): `OrderBy/OrderByDescending/ThenBy/ThenByDescending` (simple column), `Skip/Take` (compile-time constant → `OFFSET/LIMIT`), `Distinct`; terminals `ToList/First/FirstOrDefault/Single/SingleOrDefault`, zero-arg `Count/Any`, single-selector `Sum/Average/Min/Max`. Interceptor signatures declare+ignore the terminal's extra args (e.g. aggregate selector) so they match exactly; `VeloInterceptorSupport.ExecuteAggregate` runs baked aggregates. Anything else → emit nothing (VELO001). 11 generator-driver unit + 5 integration (interception with zero runtime translation + runtime equivalence). ✅
- [ ] **Phase 15c** — Parameterized compile-time queries + **true zero-boxing parameter binding** (Priority 1 #4). *Finding:* a C# interceptor's signature cannot receive a query's **closure-captured** values (they live in the `IQueryable`'s expression tree, not as method arguments), so parameterized predicates (`Where(x => x.C == localVar)`, variable `Skip/Take`) cannot be intercepted at the plain call site without boxing. The correct vehicle is **`Query.Compile<TCtx, T1, …>((ctx, p1) => …)`** where the parameters are explicit typed lambda arguments: the generator emits a delegate that builds SQL with `p1…` as bound parameters and creates **`NpgsqlParameter<T> { TypedValue = … }`** *directly in generated straight-line code* (no intermediate `object`/list → genuinely zero boxing). Requires: Roslyn predicate translation in `SymbolQueryTranslator` (comparisons, `&&/||`, member access, null checks); `Query.Compile` call-site interception returning the generated delegate; typed parameter emission. (A runtime typed-binding list cannot achieve zero boxing for a heterogeneous parameter set — only generated code can — so this lands with the codegen, not as standalone infra.) **Recommended as the first task on "continue".**
- [x] **Phase 16** — Struct-based connection/transaction wrapper. `VeloTransaction` (**`readonly struct : IAsyncDisposable`**, zero-alloc via `await using`, `CommitAsync/RollbackAsync`, auto-rollback if not committed) + sync **`ref struct`** `VeloTransactionScope`. `ICommandExecutor`/`PostgresCommandExecutor` are transaction-aware via a connection "lease" (execute on a supplied `DbTransaction`; else fresh pooled connection); `VeloDbContext.BeginTransactionAsync()`/`BeginTransaction()`. 4 integration tests (commit persists, dispose/rollback discards, sync scope). ✅
- [x] **Phase 17** — Bulk update via temp table + bulk ergonomics. `PostgresBulkUpdater`: `CREATE TEMP TABLE (LIKE main)` → binary COPY (shared `BulkCopyWriter`, reused by the inserter) → single `UPDATE main AS target SET … FROM tmp WHERE target.pk = tmp.pk` → drop temp. Transaction-aware. `VeloBulkExtensions.BulkInsert/BulkUpdate` (incl. a `VeloTransaction` overload) on the context. 3 integration tests (500-row update via temp table — no per-row UPDATE; transactional commit persists / rollback discards). ✅ *Also hardened `ScaffoldTests` to reset the public schema so it is order-independent.* **⏸ Priority 1 block complete — stop for review.**

### PRIORITY 2 — enterprise relational features (after review, on "continue")

- [ ] **Phase 18** — Many-to-Many relationships. `NavigationKind.ManyToMany` + junction metadata; `NavigationResolver` 3rd pass detects junction (pivot) tables by convention/`[ForeignKey]`; `ExpressionTranslator.ApplyInclude` + `QueryEngine.BuildCollectionPlan` add the junction JOIN in the follow-up query; fluent `HasMany(...).WithMany(...).UsingEntity(...)`.
- [ ] **Phase 19** — Explicit loading (change-tracking-free). Stateless `db.Entry(entity).Reference(o => o.User).Load()/LoadAsync()` and `.Collection(...).Load()` — no identity map / state, just an on-demand targeted query into the supplied instance using the existing Include follow-up infrastructure.
- [ ] **Phase 20** — Global query filters (soft delete, model-level). `EntityTypeBuilder<T>.HasQueryFilter(e => !e.IsDeleted)` stored on `EntityModel`; auto-injected as a root `WHERE` in `ExpressionTranslator` (and honored by the generated path); `IgnoreQueryFilters()` escape hatch. No migration impact.
- [ ] **Phase 21** — Static logging interceptors. `db.LogTo(Console.WriteLine)` — static, zero-alloc logging hook (no new delegate/instance per query) wired into the executor; SQL + parameter masking.

### Recommended extras (do if practical, else mark "future")
Connection resiliency/retry · logging/tracing with SQL+param masking · Unit-of-Work
API · optimistic concurrency (xmin) · JSON/JSONB, array, enum, Guid, DateTimeOffset
types · compiled-query handle cache · SampleApi health check · analyzer code-fix
("convert to Query.Compile").
