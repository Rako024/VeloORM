# VeloORM

> A high-performance hybrid ORM for .NET, targeting PostgreSQL.
> **Dapper-class performance with EF-class ergonomics** — code-first, migrations, type-safe LINQ.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

VeloORM is built around a **three-layer hybrid execution model**. Every query is
correct through the runtime engine; the compile-time layers are pure performance
enhancements layered on top.

1. **Runtime engine (default)** — `IQueryable` → SQL translator with a shape-keyed
   query cache. Always correct, even when no code is generated.
2. **Interceptor layer (compile-time)** — for statically-known queries, a Roslyn
   source generator + C# Interceptors bake the SQL into the assembly. ≈ zero
   runtime overhead.
3. **Fragment generation (compile-time)** — for `if (flag) q = q.Where(...)`
   optional filters, SQL fragments are prepared at compile time and cheaply
   assembled at runtime.

**Correctness principle:** when the generator is in any doubt, it emits nothing
and the query falls back to the runtime engine. Performance may vary; correctness
never does.

## Status

🚧 Early development. Currently building the **foundation block (Phases 0–3)**:
solution skeleton, core abstractions, the PostgreSQL provider, and the runtime
query engine. See [CLAUDE.md](CLAUDE.md) for the architecture and phase roadmap.

## Repository layout

```
src/
  VeloORM.Core         core abstractions, query model, metadata
  VeloORM.Runtime      runtime IQueryable provider + shape-keyed cache
  VeloORM.Generator    Roslyn source generator + interceptors (netstandard2.0)
  VeloORM.Postgres     Npgsql dialect, type mapping, COPY bulk insert
  VeloORM.Migrations   migration model, history, diff engine
  VeloORM.Scaffold     DB-first reverse engineering
  VeloORM.Cli          `velo` CLI (dotnet tool)
tests/
  VeloORM.Tests.Unit
  VeloORM.Tests.Integration   (Testcontainers + real Postgres)
samples/
  VeloORM.SampleApi    minimal ASP.NET Core Web API
docker/
  docker-compose.yml   Postgres 16 + Adminer
```

## Getting started (development)

```bash
# 1. Bring up Postgres (and Adminer at http://localhost:8080)
docker compose -f docker/docker-compose.yml up -d

# 2. Build the solution
dotnet build VeloORM.slnx

# 3. Run the tests
dotnet test
```

## Sample API

A minimal ASP.NET Core API in `samples/VeloORM.SampleApi` demonstrates all three layers
and auto-applies the code-first schema on startup.

```bash
docker compose -f docker/docker-compose.yml up -d        # Postgres
dotnet run --project samples/VeloORM.SampleApi           # API on http://localhost:5xxx
```

Endpoints (each maps to a layer):

| Endpoint | Layer |
|---|---|
| `GET /products` | interceptor (compile-time SQL) |
| `GET /products/{id}` | runtime engine (`Where`) |
| `GET /products/search?name=&minPrice=&inStock=` | fragment (bool-gated filters) |
| `GET /products/expensive?min=` | raw SQL (`db.Query`) |
| `POST /products` | raw SQL insert (`RETURNING id`) |
| `GET /health` | DB connectivity |

The OpenAPI document is served at `/openapi/v1.json`. The connection string comes from
`ConnectionStrings:Postgres`, the `VELO_CONNECTION` env var, or a localhost default.

## Benchmarks

A BenchmarkDotNet harness comparing VeloORM, Dapper, and EF Core lives in
`benchmarks/VeloORM.Benchmarks`. With Postgres up:

```bash
VELO_CONNECTION="Host=localhost;Username=velo;Password=velo_dev_password;Database=veloorm" \
  dotnet run -c Release --project benchmarks/VeloORM.Benchmarks
```

## Requirements

- .NET SDK 8.0+ (build uses the .NET 10 SDK; runtime libraries multi-target
  `net8.0` and `net10.0`)
- Docker (for the integration test suite and local Postgres)

## License

[MIT](LICENSE)
