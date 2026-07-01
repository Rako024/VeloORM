using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using VeloORM.Benchmarks;

// A deliberately short job: fixed iteration/warmup counts so BenchmarkDotNet does NOT auto-adjust up to
// ~100 iterations (which made a full run take hours). ~3 warmup + 10 measured iterations per method is
// plenty for a stable relative comparison across ADO.NET / Dapper / EF Core / VeloORM.
// Override on the CLI if you want more rigor, e.g. `-- --warmupCount 5 --iterationCount 20`.
var config = DefaultConfig.Instance.AddJob(
    Job.Default
        .WithWarmupCount(3)
        .WithIterationCount(10)
        .WithLaunchCount(1));

// Run all benchmark classes, or filter: `-- --filter *Flat*` / `*Aggregate*` / `*Bulk*` etc.
BenchmarkSwitcher.FromAssembly(typeof(FlatBenchmarks).Assembly).Run(args, config);
