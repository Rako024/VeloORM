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

> **Current scope:** Phases 0–3 (foundation block), then stop for review.

- [x] **Phase 0** — Skeleton & infrastructure (solution, projects, Directory.Build.props, docker-compose, README/LICENSE/CLAUDE.md). Compiles; `docker compose up -d` works. ✅
- [x] **Phase 1** — Core abstractions (IDbContext, DbSet<T>, metadata model, ISqlDialect, query model AST, IMaterializer). Unit tests pass (19 tests). ✅
- [x] **Phase 2** — Postgres dialect + manual materializer (Npgsql, PostgresDialect, type mapping). Integration round-trip test passes (3 tests). ✅
- [x] **Phase 3** — Runtime engine ⭐ (IQueryable provider, expression→SQL, bound params, shape-keyed cache). Integration tests pass (15 incl. warm-cache no-recompile). ✅ *Single-table operators (Where/Select/OrderBy/ThenBy/Take/Skip/First/Single/Any/Count + string methods, IN, null checks) are implemented; Join/GroupBy currently throw `NotSupportedException` (fallback-safe, no wrong SQL) — to be completed.*
- [x] **Phase 4** — Raw SQL escape hatch + `[InterpolatedStringHandler]` (`VeloInterpolatedSql`; `Query`/`QueryAsync`/`QuerySingleOrDefault`/`Execute`/`ExecuteAsync`/`ExecuteScalar`). 6 integration tests (function + view + injection). ✅
- [x] **Phase 5** — Source generator: interceptor layer. Incremental generator intercepts static `db.Set<T>().ToList()/First()/Single()/Count()/Any()` (no predicate/operators) via `GetInterceptableLocation()` + `[InterceptsLocation]`, baking compile-time SQL + reflection-free materializer; verified zero runtime translation. ✅ *Predicate/operator chains bail to runtime (correctness principle); compile-time predicate translation is the next enhancement.*
- [ ] **Phase 6** — Fragment generation (bool-gated optional filters).
- [ ] **Phase 7** — Diagnostics (VELO001) + `Query.Compile` opt-in (VELO002).
- [ ] **Phase 8** — Code-first schema + migrations.
- [ ] **Phase 9** — DB-first scaffolding.
- [ ] **Phase 10** — CLI (`velo`) as a dotnet tool.
- [ ] **Phase 11** — Bulk (`COPY`) & performance; BenchmarkDotNet vs Dapper/EF.
- [ ] **Phase 12** — SampleApi demonstrating all three layers.
- [ ] **Phase 13** — NuGet packaging.

### Recommended extras (do if practical, else mark "future")
Connection resiliency/retry · logging/tracing with SQL+param masking · Unit-of-Work
API · optimistic concurrency (xmin) · JSON/JSONB, array, enum, Guid, DateTimeOffset
types · compiled-query handle cache · SampleApi health check · analyzer code-fix
("convert to Query.Compile").
